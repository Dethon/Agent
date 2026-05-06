using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Tools.SubAgents;

public class SubAgentReleaseTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_release";

    public const string Description =
        "Releases a terminal (completed, failed, or cancelled) subagent session, freeing its resources. " +
        "Returns an error if the session is still running or does not exist.";

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

        try
        {
            var released = featureConfig.SubAgentSessions.Release(handle);
            if (!released)
            {
                return Task.FromResult<JsonNode>(ToolError.Create(
                    ToolError.Codes.NotFound,
                    $"No subagent session found for handle '{handle}'",
                    retryable: false));
            }

            return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "released" });
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult<JsonNode>(ToolError.Create(
                "invalid_operation",
                ex.Message,
                retryable: false));
        }
    }
}
