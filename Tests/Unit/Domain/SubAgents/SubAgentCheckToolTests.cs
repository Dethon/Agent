using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Domain.Tools.SubAgents;
using Shouldly;
using System.Text.Json.Nodes;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentCheckToolTests
{
    private static SubAgentSessionView MakeCompletedView() => new()
    {
        Handle = "h-abc",
        SubAgentId = "researcher",
        Status = SubAgentStatus.Completed,
        StartedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        ElapsedSeconds = 7.5,
        Turns =
        [
            new SubAgentTurnSnapshot
            {
                Index = 0,
                AssistantText = "I'll look into that.",
                ToolCalls = [new SubAgentToolCallSummary("web_search", "query=foo")],
                ToolResults = [new SubAgentToolResultSummary("web_search", Ok: true, "3 results found")],
                StartedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 7, TimeSpan.Zero)
            }
        ],
        Result = "ok",
        Error = null,
        CancelledBy = null
    };

    [Fact]
    public async Task RunAsync_KnownHandle_ReturnsViewWithSnapshots()
    {
        var view = MakeCompletedView();
        var sessions = new FakeSessions(view);
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentCheckTool(config);

        var result = await tool.RunAsync("h-abc");

        result["status"]!.ToString().ShouldBe("completed");
        result["handle"]!.ToString().ShouldBe("h-abc");
        result["subagent_id"]!.ToString().ShouldBe("researcher");
        result["started_at"]!.ToString().ShouldBe("2026-01-01T12:00:00.0000000+00:00");
        result["elapsed_seconds"]!.GetValue<double>().ShouldBe(7.5);

        var turns = result["turns"]!.AsArray();
        turns.Count.ShouldBe(1);
        var turn = turns[0]!.AsObject();
        turn["index"]!.GetValue<int>().ShouldBe(0);
        turn["assistant_text"]!.ToString().ShouldBe("I'll look into that.");

        var toolCalls = turn["tool_calls"]!.AsArray();
        toolCalls.Count.ShouldBe(1);
        toolCalls[0]!["name"]!.ToString().ShouldBe("web_search");
        toolCalls[0]!["args_summary"]!.ToString().ShouldBe("query=foo");

        var toolResults = turn["tool_results"]!.AsArray();
        toolResults.Count.ShouldBe(1);
        toolResults[0]!["name"]!.ToString().ShouldBe("web_search");
        toolResults[0]!["ok"]!.GetValue<bool>().ShouldBeTrue();
        toolResults[0]!["summary"]!.ToString().ShouldBe("3 results found");

        result["result"]!.ToString().ShouldBe("ok");
        result["error"].ShouldBeNull();
        result["cancelled_by"].ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_UnknownHandle_ReturnsNotFound()
    {
        var sessions = new FakeSessions(null);
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentCheckTool(config);

        var result = await tool.RunAsync("unknown-handle");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("not_found");
    }

    [Fact]
    public async Task RunAsync_NoSessions_ReturnsUnavailable()
    {
        var config = new FeatureConfig(SubAgentSessions: null);
        var tool = new SubAgentCheckTool(config);

        var result = await tool.RunAsync("h-xyz");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("unavailable");
    }

    private sealed class FakeSessions(SubAgentSessionView? view) : ISubAgentSessions
    {
        public int ActiveCount => 0;

        public string Start(SubAgentDefinition profile, string prompt, bool silent) => "h-new";
        public SubAgentSessionView? Get(string handle) => view;
        public IReadOnlyList<SubAgentSessionView> List() => view is null ? [] : [view];
        public void Cancel(string handle, SubAgentCancelSource source) { }
        public Task<SubAgentWaitResult> WaitAsync(IReadOnlyList<string> handles, SubAgentWaitMode mode,
            TimeSpan timeout, CancellationToken ct) => Task.FromResult(new SubAgentWaitResult([], []));
        public bool Release(string handle) => false;
    }
}
