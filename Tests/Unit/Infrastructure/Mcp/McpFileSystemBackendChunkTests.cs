using System.Text.Json.Nodes;
using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

public class McpFileSystemBackendChunkTests
{
    [Fact]
    public async Task ReadChunksAsync_YieldsFirstChunkBeforeReadingRest()
    {
        // Simulates a ~10 MB file: 40 full 256 KiB chunks then EOF on the 41st call.
        var backend = new CountingBackend(totalChunks: 40);

        var enumerator = backend.ReadChunksAsync("any.bin", CancellationToken.None).GetAsyncEnumerator();
        try
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();

            enumerator.Current.Length.ShouldBe(256 * 1024);
            backend.CallCount.ShouldBe(1);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private sealed class CountingBackend(int totalChunks) : McpFileSystemBackend(null!, "test")
    {
        public int CallCount { get; private set; }

        protected internal override Task<JsonNode> CallToolAsync(
            string toolName, Dictionary<string, object?> args, CancellationToken ct)
        {
            CallCount++;
            var bytes = new byte[256 * 1024];
            var eof = CallCount > totalChunks;
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["contentBase64"] = Convert.ToBase64String(bytes),
                ["eof"] = eof
            });
        }
    }
}