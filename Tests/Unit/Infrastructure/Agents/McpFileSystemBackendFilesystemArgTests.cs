using System.Text.Json.Nodes;
using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class McpFileSystemBackendFilesystemArgTests
{
    private sealed class CapturingBackend() : McpFileSystemBackend(null!, "downloads")
    {
        public List<(string Tool, Dictionary<string, object?> Args)> Calls { get; } = [];

        protected internal override Task<JsonNode> CallToolAsync(
            string toolName, Dictionary<string, object?> args, CancellationToken ct)
        {
            Calls.Add((toolName, args));
            JsonNode payload = toolName switch
            {
                "fs_blob_read" => new JsonObject { ["contentBase64"] = "", ["eof"] = true },
                _ => new JsonObject
                {
                    ["entries"] = new JsonArray(),
                    ["truncated"] = false,
                    ["total"] = 0
                }
            };
            return Task.FromResult(payload);
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyChunks()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Fact]
    public async Task EveryCall_CarriesTheFilesystemName()
    {
        var backend = new CapturingBackend();

        await backend.GlobAsync("/", "*", CancellationToken.None);
        await backend.GlobAsync("/sub", "*.json", CancellationToken.None);

        backend.Calls.ShouldAllBe(c => Equals(c.Args["filesystem"], "downloads"));
    }

    [Fact]
    public async Task ReadChunks_CarriesTheFilesystemName()
    {
        var backend = new CapturingBackend();

        await foreach (var _ in backend.ReadChunksAsync("42/x", CancellationToken.None))
        {
        }

        backend.Calls.ShouldContain(c => c.Tool == "fs_blob_read");
        backend.Calls
            .Where(c => c.Tool == "fs_blob_read")
            .ShouldAllBe(c => Equals(c.Args["filesystem"], "downloads"));
    }

    [Fact]
    public async Task WriteChunks_EmptySource_CarriesTheFilesystemName()
    {
        var backend = new CapturingBackend();

        await backend.WriteChunksAsync("42/x", EmptyChunks(), true, true, CancellationToken.None);

        backend.Calls.ShouldContain(c => c.Tool == "fs_blob_write");
        backend.Calls
            .Where(c => c.Tool == "fs_blob_write")
            .ShouldAllBe(c => Equals(c.Args["filesystem"], "downloads"));
    }
}