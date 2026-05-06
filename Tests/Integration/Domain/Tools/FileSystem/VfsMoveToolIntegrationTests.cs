using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

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

    private static VirtualFileSystemRegistry BuildRegistry(McpClient libClient, McpClient notesClient)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount("library", "/library", "lib"), new McpFileSystemBackend(libClient, "library"));
        registry.Mount(new FileSystemMount("notes", "/notes", "notes"), new McpFileSystemBackend(notesClient, "notes"));
        return registry;
    }

    private static async Task<McpClient> Connect(string endpoint)
        => await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
}
