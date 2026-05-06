using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Tools.SubAgents;

public class SubAgentListTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_list";

    public const string Description =
        "Returns a compact summary of all background subagent sessions. " +
        "Each entry includes handle, subagent_id, status, started_at, and elapsed_seconds.";

    public JsonNode RunAsync(CancellationToken ct = default)
    {
        if (featureConfig.SubAgentSessions is null)
        {
            return ToolError.Create(
                ToolError.Codes.Unavailable,
                "Background subagent sessions are not available in this context",
                retryable: false);
        }

        return new JsonArray(
            featureConfig.SubAgentSessions.List()
                .Select(v => (JsonNode)new JsonObject
                {
                    ["handle"] = v.Handle,
                    ["subagent_id"] = v.SubAgentId,
                    ["status"] = v.Status.ToString().ToLowerInvariant(),
                    ["started_at"] = v.StartedAt.ToString("O"),
                    ["elapsed_seconds"] = v.ElapsedSeconds
                })
                .ToArray());
    }
}
