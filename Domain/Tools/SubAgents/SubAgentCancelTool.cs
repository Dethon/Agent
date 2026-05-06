using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Tools.SubAgents;

public class SubAgentCancelTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_cancel";

    public const string Description =
        "Requests cancellation of a running background subagent session. " +
        "Returns immediately with status 'cancelling'; the session reaches a terminal state asynchronously. " +
        "If the session is already terminal, returns its current status without re-cancelling.";

    public Task<JsonNode> RunAsync(
        [Description("Handle returned by run_subagent")]
        string handle,
        CancellationToken ct = default)
    {
        if (featureConfig.SubAgentSessions is null)
        {
            return Task.FromResult<JsonNode>(ToolError.Create(
                ToolError.Codes.Unavailable,
                "Background subagent sessions are not available in this context",
                retryable: false));
        }

        var view = featureConfig.SubAgentSessions.Get(handle);
        if (view is null)
        {
            return Task.FromResult<JsonNode>(ToolError.Create(
                ToolError.Codes.NotFound,
                $"No subagent session found for handle '{handle}'",
                retryable: false));
        }

        if (view.Status != SubAgentStatus.Running)
        {
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["status"] = view.Status.ToString().ToLowerInvariant(),
                ["handle"] = handle
            });
        }

        featureConfig.SubAgentSessions.Cancel(handle, SubAgentCancelSource.Parent);

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["status"] = "cancelling",
            ["handle"] = handle
        });
    }
}
