using Domain.DTOs.SubAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent.Services.SubAgents;

public sealed class SnapshotRecorder
{
    private const int ArgsCap = 200;
    private const int ResultCap = 500;
    public const int MaxRetainedTurns = 50;

    public string FinalAssistantText { get; private set; } = "";

    private int _index;
    private DateTimeOffset _turnStart = DateTimeOffset.UtcNow;
    private readonly System.Text.StringBuilder _assistantTextBuf = new();
    private readonly List<SubAgentToolCallSummary> _calls = [];
    private readonly List<SubAgentToolResultSummary> _results = [];
    private bool _sawToolResultThisTurn;

    public SubAgentTurnSnapshot? OnUpdate(AgentResponseUpdate update)
    {
        SubAgentTurnSnapshot? completed = null;

        if (update.Role == ChatRole.Assistant && _sawToolResultThisTurn)
        {
            completed = BuildAndReset();
        }

        if (update.Role == ChatRole.Assistant)
        {
            foreach (var c in update.Contents)
            {
                if (c is TextContent tc) _assistantTextBuf.Append(tc.Text);
                else if (c is FunctionCallContent fc)
                    _calls.Add(new SubAgentToolCallSummary(
                        fc.Name,
                        Truncate(fc.Arguments?.ToString() ?? "", ArgsCap)));
            }
        }
        else if (update.Role == ChatRole.Tool)
        {
            _sawToolResultThisTurn = true;
            foreach (var c in update.Contents)
            {
                if (c is FunctionResultContent fr)
                    _results.Add(new SubAgentToolResultSummary(
                        fr.CallId ?? "(tool)",
                        fr.Exception is null,
                        Truncate(fr.Result?.ToString() ?? "", ResultCap)));
            }
        }

        return completed;
    }

    public SubAgentTurnSnapshot? Flush()
    {
        if (_assistantTextBuf.Length == 0 && _calls.Count == 0 && _results.Count == 0) return null;
        FinalAssistantText = _assistantTextBuf.ToString();
        return BuildAndReset();
    }

    private SubAgentTurnSnapshot BuildAndReset()
    {
        var snap = new SubAgentTurnSnapshot
        {
            Index = _index++,
            AssistantText = _assistantTextBuf.ToString(),
            ToolCalls = _calls.ToArray(),
            ToolResults = _results.ToArray(),
            StartedAt = _turnStart,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _assistantTextBuf.Clear();
        _calls.Clear();
        _results.Clear();
        _sawToolResultThisTurn = false;
        _turnStart = DateTimeOffset.UtcNow;
        return snap;
    }

    private static string Truncate(string s, int cap) => s.Length <= cap ? s : s[..cap] + "…";
}
