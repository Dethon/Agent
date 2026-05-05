using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Infrastructure.Mcp;

[Collection("MultiFileSystem")]
public class McpFileSystemBackendStreamTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task OpenReadStreamAsync_LargeFile_ReadsAllBytesCorrectly()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "big.bin"), bytes);

        await using var client = await CreateClient(fx.LibraryEndpoint);
        var backend = new McpFileSystemBackend(client, "library");

        await using var stream = await backend.OpenReadStreamAsync("big.bin", CancellationToken.None);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        ms.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task WriteFromStreamAsync_LargeFile_WritesAllBytes()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        await using var client = await CreateClient(fx.NotesEndpoint);
        var backend = new McpFileSystemBackend(client, "notes");

        await using var input = new MemoryStream(bytes);
        await backend.WriteFromStreamAsync("written.bin", input,
            overwrite: false, createDirectories: true, CancellationToken.None);

        File.ReadAllBytes(Path.Combine(fx.NotesPath, "written.bin")).ShouldBe(bytes);
    }

    private static async Task<McpClient> CreateClient(string endpoint)
    {
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
    }
}
