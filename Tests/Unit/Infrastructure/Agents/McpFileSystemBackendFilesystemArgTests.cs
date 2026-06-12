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
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["entries"] = new JsonArray(),
                ["truncated"] = false,
                ["total"] = 0
            });
        }
    }

    [Fact]
    public async Task EveryCall_CarriesTheFilesystemName()
    {
        var backend = new CapturingBackend();

        await backend.GlobAsync("/", "*", CancellationToken.None);
        await backend.GlobAsync("/sub", "*.json", CancellationToken.None);

        backend.Calls.ShouldAllBe(c => Equals(c.Args["filesystem"], "downloads"));
    }
}