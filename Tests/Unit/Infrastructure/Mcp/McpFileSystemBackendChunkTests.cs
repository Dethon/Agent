using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools;
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

    // The far end answers a refusal as an envelope, and the chunk path has none to pass it on in.
    // Flattening it to a bare IOException lost the code, and the cross-mount copy then presented a
    // permanent refusal as internal_error / retryable — so the error travels as a typed exception.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChunkOperations_WhenTheServerRefuses_CarryTheEnvelopeThrough(bool reading)
    {
        var backend = new RefusingBackend();

        var thrown = await Should.ThrowAsync<FileSystemOperationException>(async () =>
        {
            if (reading)
            {
                await foreach (var _ in backend.ReadChunksAsync("any.bin", CancellationToken.None))
                {
                }
            }
            else
            {
                await backend.WriteChunksAsync("any.bin", Chunks(), true, true, CancellationToken.None);
            }
        });

        thrown.Error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        thrown.Error.Retryable.ShouldBeFalse();
        thrown.Message.ShouldContain("Access denied");
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks()
    {
        await Task.CompletedTask;
        yield return new byte[] { 1, 2, 3 };
    }

    private sealed class RefusingBackend() : McpFileSystemBackend(null!, "test", advertisedOperations: null)
    {
        protected internal override Task<JsonNode> CallToolAsync(
            string toolName, Dictionary<string, object?> args, CancellationToken ct) =>
            Task.FromResult<JsonNode>(new JsonObject
            {
                ["ok"] = false,
                ["errorCode"] = ToolError.Codes.InvalidArgument,
                ["message"] = "Access denied: path must be within /vault",
                ["retryable"] = false
            });
    }

    private sealed class CountingBackend(int totalChunks) : McpFileSystemBackend(null!, "test", advertisedOperations: null)
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