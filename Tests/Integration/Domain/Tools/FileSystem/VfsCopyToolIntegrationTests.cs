using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Domain.Tools.FileSystem;

[Collection("MultiFileSystem")]
public class VfsCopyToolIntegrationTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task RunAsync_CrossFsTextFile_CopiesAndPreservesSource()
    {
        fx.CreateLibraryFile("hello.md", "from library");
        await using var libClient = await Connect(fx.LibraryEndpoint);
        await using var notesClient = await Connect(fx.NotesEndpoint);
        var registry = BuildRegistry(libClient, notesClient);
        var tool = new VfsCopyTool(registry);

        var result = await tool.RunAsync("/library/hello.md", "/notes/hello.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        File.Exists(Path.Combine(fx.LibraryPath, "hello.md")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(fx.NotesPath, "hello.md")).ShouldBe("from library");
    }

    [Fact]
    public async Task RunAsync_CrossFsBinaryFile_RoundtripsAllBytes()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "blob.bin"), bytes);
        await using var libClient = await Connect(fx.LibraryEndpoint);
        await using var notesClient = await Connect(fx.NotesEndpoint);
        var registry = BuildRegistry(libClient, notesClient);
        var tool = new VfsCopyTool(registry);

        // The destination mount takes text extensions, so the transfer is named as one; the point
        // here is that every byte survives the streamed round trip, not what the name says.
        var result = await tool.RunAsync("/library/blob.bin", "/notes/blob.txt");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        File.ReadAllBytes(Path.Combine(fx.NotesPath, "blob.txt")).ShouldBe(bytes);
    }

    // The destination mount's allowed extensions govern the streamed write as much as fs_create, and
    // the refusal has to arrive as the backend's own envelope: a disallowed extension is permanent,
    // and the copy tool used to relabel everything the stream threw as internal_error / retryable.
    [Fact]
    public async Task RunAsync_CrossFsDisallowedExtension_ReturnsThePermanentRefusal()
    {
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "refused.bin"), [1, 2, 3]);
        await using var libClient = await Connect(fx.LibraryEndpoint);
        await using var notesClient = await Connect(fx.NotesEndpoint);
        var tool = new VfsCopyTool(BuildRegistry(libClient, notesClient));

        var result = await tool.RunAsync("/library/refused.bin", "/notes/refused.bin");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
        File.Exists(Path.Combine(fx.NotesPath, "refused.bin")).ShouldBeFalse();
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