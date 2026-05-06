using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Tools.SubAgents;

public class SubAgentWaitTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_wait";

    public const string Description =
        "Blocks until one or more background subagent sessions reach a terminal state. " +
        "Use mode='any' to return when the first session finishes, mode='all' to wait for all. " +
        "Returns a partition of handles into completed and still_running lists.";

    public Task<JsonNode> RunAsync(
        [Description("Handles returned by run_subagent (background mode)")]
        string[] handles,
        [Description("'any' to return when the first session finishes, 'all' to wait for all (default: 'all')")]
        string mode = "all",
        [Description("Maximum seconds to wait before returning (1–600, default: 60)")]
        int timeout_seconds = 60,
        CancellationToken ct = default)
    {
        if (featureConfig.SubAgentSessions is null)
        {
            return Task.FromResult<JsonNode>(ToolError.Create(
                ToolError.Codes.Unavailable,
                "Background subagent sessions are not available in this context",
                retryable: false));
        }

        if (timeout_seconds is < 1 or > 600)
        {
            return Task.FromResult<JsonNode>(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "timeout_seconds must be between 1 and 600",
                retryable: false));
        }

        if (!Enum.TryParse<SubAgentWaitMode>(mode, ignoreCase: true, out var waitMode))
        {
            return Task.FromResult<JsonNode>(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                $"Invalid mode '{mode}'. Valid values are: any, all",
                retryable: false));
        }

        return WaitAndSerializeAsync(handles, waitMode, TimeSpan.FromSeconds(timeout_seconds), ct);
    }

    private async Task<JsonNode> WaitAndSerializeAsync(
        string[] handles, SubAgentWaitMode waitMode, TimeSpan timeout, CancellationToken ct)
    {
        var result = await featureConfig.SubAgentSessions!.WaitAsync(handles, waitMode, timeout, ct);

        return new JsonObject
        {
            ["completed"] = new JsonArray(result.Completed.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
            ["still_running"] = new JsonArray(result.StillRunning.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray())
        };
    }
}
