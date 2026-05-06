using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Tools.SubAgents;

public class SubAgentCheckTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_check";

    public const string Description =
        "Returns the current status and turn snapshots of a background subagent session. " +
        "Use the handle returned by run_subagent (background mode).";

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

        return Task.FromResult<JsonNode>(SerializeView(view));
    }

    public static JsonObject SerializeView(SubAgentSessionView v)
    {
        var obj = new JsonObject
        {
            ["status"] = v.Status.ToString().ToLowerInvariant(),
            ["handle"] = v.Handle,
            ["subagent_id"] = v.SubAgentId,
            ["started_at"] = v.StartedAt.ToString("O"),
            ["elapsed_seconds"] = v.ElapsedSeconds,
            ["turns"] = new JsonArray(v.Turns.Select(SerializeTurn).ToArray<JsonNode?>())
        };

        if (v.Result is not null)
            obj["result"] = v.Result;

        if (v.Error is not null)
            obj["error"] = new JsonObject
            {
                ["code"] = v.Error.Code,
                ["message"] = v.Error.Message
            };

        if (v.CancelledBy is not null)
            obj["cancelled_by"] = v.CancelledBy.ToString()!.ToLowerInvariant();

        return obj;
    }

    private static JsonObject SerializeTurn(SubAgentTurnSnapshot t) => new()
    {
        ["index"] = t.Index,
        ["assistant_text"] = t.AssistantText,
        ["tool_calls"] = new JsonArray(t.ToolCalls.Select(c => (JsonNode?)new JsonObject
        {
            ["name"] = c.Name,
            ["args_summary"] = c.ArgsSummary
        }).ToArray()),
        ["tool_results"] = new JsonArray(t.ToolResults.Select(r => (JsonNode?)new JsonObject
        {
            ["name"] = r.Name,
            ["ok"] = r.Ok,
            ["summary"] = r.Summary
        }).ToArray()),
        ["started_at"] = t.StartedAt.ToString("O"),
        ["completed_at"] = t.CompletedAt.ToString("O")
    };
}
