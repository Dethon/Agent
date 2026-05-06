using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Infrastructure.Mcp;

[Collection("MultiFileSystem")]
public class McpFileSystemBackendCopyTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task CopyAsync_FileWithinSameBackend_CopiesContent()
    {
        fx.CreateLibraryFile("note.md", "hello");
        await using var client = await CreateClient(fx.LibraryEndpoint);
        var backend = new McpFileSystemBackend(client, "library");

        var result = await backend.CopyAsync("note.md", "note-copy.md",
            overwrite: false, createDirectories: true, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("copied");
        File.ReadAllText(Path.Combine(fx.LibraryPath, "note-copy.md")).ShouldBe("hello");
    }

    [Fact]
    public async Task CopyAsync_DirectoryWithinSameBackend_CopiesRecursively()
    {
        fx.CreateLibraryFile("src/a.md", "A");
        fx.CreateLibraryFile("src/sub/b.md", "B");
        await using var client = await CreateClient(fx.LibraryEndpoint);
        var backend = new McpFileSystemBackend(client, "library");

        var result = await backend.CopyAsync("src", "dst",
            overwrite: false, createDirectories: true, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("copied");
        File.ReadAllText(Path.Combine(fx.LibraryPath, "dst", "a.md")).ShouldBe("A");
        File.ReadAllText(Path.Combine(fx.LibraryPath, "dst", "sub", "b.md")).ShouldBe("B");
    }

    private static async Task<McpClient> CreateClient(string endpoint)
    {
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
    }
}

[CollectionDefinition("MultiFileSystem")]
public class MultiFileSystemCollection : ICollectionFixture<MultiFileSystemFixture> { }
