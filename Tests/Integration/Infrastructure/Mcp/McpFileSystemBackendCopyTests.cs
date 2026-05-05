using Domain.DTOs;
using Infrastructure.Agents;
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
        var backend = await CreateBackend(fx.LibraryEndpoint, "library");

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
        var backend = await CreateBackend(fx.LibraryEndpoint, "library");

        await backend.CopyAsync("src", "dst",
            overwrite: false, createDirectories: true, CancellationToken.None);

        File.ReadAllText(Path.Combine(fx.LibraryPath, "dst", "a.md")).ShouldBe("A");
        File.ReadAllText(Path.Combine(fx.LibraryPath, "dst", "sub", "b.md")).ShouldBe("B");
    }

    private static async Task<McpFileSystemBackend> CreateBackend(string endpoint, string name)
    {
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
        return new McpFileSystemBackend(client, name);
    }
}

[CollectionDefinition("MultiFileSystem")]
public class MultiFileSystemCollection : ICollectionFixture<MultiFileSystemFixture> { }
