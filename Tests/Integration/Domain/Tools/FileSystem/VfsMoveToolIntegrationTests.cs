using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.Domain.Downloads.Vfs;

namespace Tests.Integration.Domain.Tools.FileSystem;

[Collection("MultiFileSystem")]
public class VfsMoveToolIntegrationTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task RunAsync_CrossFsDirectory_MovesAllFilesAndRemovesSource()
    {
        fx.CreateLibraryFile("project/a.md", "alpha");
        fx.CreateLibraryFile("project/sub/b.md", "beta");
        await using var libClient = await Connect(fx.LibraryEndpoint);
        await using var notesClient = await Connect(fx.NotesEndpoint);
        var registry = BuildRegistry(libClient, notesClient);
        var tool = new VfsMoveTool(registry);

        var result = await tool.RunAsync("/library/project", "/notes/project");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(2);
        Directory.Exists(Path.Combine(fx.LibraryPath, "project")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(fx.NotesPath, "project", "a.md")).ShouldBe("alpha");
        File.ReadAllText(Path.Combine(fx.NotesPath, "project", "sub", "b.md")).ShouldBe("beta");
    }

    // The topology every cross-mount bug lives in, and the one nothing exercised before: a mount
    // with refusals of its own beside a plain filesystem, both discovered and mounted the way the
    // agent really does it — so the source here is an McpFileSystemBackend proxy, which is what made
    // the old type-tested guard unreachable in every deployment.
    //
    // The three promises together: the agent is told why, the destination holds nothing, and the
    // download is still running. Before the check, this move streamed whatever files the download
    // had written and then deleted the source — which on the media mount is the documented cancel.
    [Fact]
    public async Task RunAsync_CrossFsMoveOfALiveDownload_IsRefusedBeforeAnythingIsStreamed()
    {
        fx.Downloads.Items.Clear();
        fx.Downloads.Add(DownloadFakes.Item(id: 7));
        fx.CreateMediaFile("downloads/7/payload.mkv", "half a movie");
        await using var mediaClient = await Connect(fx.MediaEndpoint);
        await using var notesClient = await Connect(fx.NotesEndpoint);
        var registry = new VirtualFileSystemRegistry();
        await McpFileSystemDiscovery.DiscoverAndMountAsync(
            [mediaClient, notesClient], registry, NullLogger.Instance, CancellationToken.None);

        var result = await new VfsMoveTool(registry).RunAsync("/media/downloads/7", "/notes/7");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["message"]!.GetValue<string>().ShouldContain("downloads/7");
        result["message"]!.GetValue<string>().ShouldContain("live download");
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
        Directory.Exists(Path.Combine(fx.NotesPath, "7")).ShouldBeFalse();
        fx.Downloads.CleanedUp.ShouldBeEmpty();
        fx.Downloads.Items.ShouldContain(i => i.Id == 7);
        File.Exists(Path.Combine(fx.MediaPath, "downloads", "7", "payload.mkv")).ShouldBeTrue();
    }

    // The same seam, the same proxy, a path no live download owns: the mount with a rule still moves
    // everything the rule does not cover.
    [Fact]
    public async Task RunAsync_CrossFsMoveOfAnOrdinaryMediaFile_StillTransfers()
    {
        fx.Downloads.Items.Clear();
        fx.Downloads.Add(DownloadFakes.Item(id: 7));
        fx.CreateMediaFile("Movies/film.mkv", "a whole movie");
        await using var mediaClient = await Connect(fx.MediaEndpoint);
        await using var notesClient = await Connect(fx.NotesEndpoint);
        var registry = new VirtualFileSystemRegistry();
        await McpFileSystemDiscovery.DiscoverAndMountAsync(
            [mediaClient, notesClient], registry, NullLogger.Instance, CancellationToken.None);

        await new VfsMoveTool(registry).RunAsync("/media/Movies/film.mkv", "/notes/film.mkv");

        File.ReadAllText(Path.Combine(fx.NotesPath, "film.mkv")).ShouldBe("a whole movie");
    }

    private static VirtualFileSystemRegistry BuildRegistry(McpClient libClient, McpClient notesClient)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount("library", "/library", "lib"), new McpFileSystemBackend(libClient, "library", advertisedOperations: null));
        registry.Mount(new FileSystemMount("notes", "/notes", "notes"), new McpFileSystemBackend(notesClient, "notes", advertisedOperations: null));
        return registry;
    }

    private static async Task<McpClient> Connect(string endpoint)
        => await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
}