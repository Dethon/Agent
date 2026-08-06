using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

// The agent never holds a real backend: every mount it has is this proxy to a server in another
// process. So the move-out check reaches the mount only if the proxy asks it — and asks it of the
// mounts that have a rule, which is exactly the ones whose server registered the tool.
public class McpFileSystemBackendMoveOutCheckTests
{
    [Fact]
    public async Task MoveOutCheckAsync_WhenTheClientAdvertisedTheTool_AsksItAndCarriesTheRefusalBack()
    {
        var backend = new RecordingBackend([FileSystemOperations.MoveOutCheck], refuse: true);

        var error = (await backend.MoveOutCheckAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Err>().Error;

        backend.Calls.ShouldBe([FileSystemOperations.MoveOutCheck]);
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("live download");
        error.Retryable.ShouldBeFalse();
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MoveOutCheckAsync_WhenTheServerAllows_ReturnsTheAllowance()
    {
        var backend = new RecordingBackend([FileSystemOperations.MoveOutCheck], refuse: false);

        (await backend.MoveOutCheckAsync("Movies/film.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Ok>();
    }

    // A mount that never registered the check has no rule to state, so silence is "allowed" rather
    // than "refused" — otherwise adding the operation would block every cross-mount move out of the
    // vault, the sandbox and the timers, none of which will ever have one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveOutCheckAsync_WhenTheClientNeverAdvertisedTheTool_AllowsWithoutCalling(bool advertisedNothing)
    {
        var backend = new RecordingBackend(
            advertisedNothing ? null : ["fs_move", "fs_delete"], refuse: true);

        (await backend.MoveOutCheckAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Ok>();

        backend.Calls.ShouldBeEmpty();
    }

    // A server may publish its tools under a prefixed name, and the capability map already reads
    // the suffix. The proxy must read it the same way, or a prefixed mount silently loses its rule.
    [Fact]
    public async Task MoveOutCheckAsync_WhenTheToolIsAdvertisedUnderAPrefixedName_StillAsks()
    {
        var backend = new RecordingBackend([$"library__{FileSystemOperations.MoveOutCheck}"], refuse: true);

        (await backend.MoveOutCheckAsync("downloads/42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Err>();
    }

    private sealed class RecordingBackend(IReadOnlyList<string>? advertised, bool refuse)
        : McpFileSystemBackend(null!, "media", advertised is null ? null : McpFileSystemDiscovery.AdvertisedOperations(advertised))
    {
        public List<string> Calls { get; } = [];

        protected internal override Task<JsonNode> CallToolAsync(
            string toolName, Dictionary<string, object?> args, CancellationToken ct)
        {
            Calls.Add(toolName);
            return Task.FromResult<JsonNode>(refuse
                ? new JsonObject
                {
                    ["ok"] = false,
                    ["errorCode"] = ToolError.Codes.UnsupportedOperation,
                    ["message"] = $"'{args["path"]}' belongs to a live download",
                    ["retryable"] = false,
                    ["hint"] = "Wait for the download to finish."
                }
                : new JsonObject { ["path"] = args["path"]!.ToString() });
        }
    }
}