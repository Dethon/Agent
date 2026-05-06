using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Domain.Tools.SubAgents;
using Shouldly;
using System.Text.Json.Nodes;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentCancelToolTests
{
    private static SubAgentSessionView MakeView(SubAgentStatus status) => new()
    {
        Handle = "h-abc",
        SubAgentId = "researcher",
        Status = status,
        StartedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        ElapsedSeconds = 3.0,
        Turns = [],
        Result = status == SubAgentStatus.Completed ? "done" : null,
        Error = null,
        CancelledBy = null
    };

    [Fact]
    public async Task RunAsync_KnownHandle_CallsCancelWithParentSource()
    {
        var view = MakeView(SubAgentStatus.Running);
        var sessions = new FakeSessions(view);
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentCancelTool(config);

        var result = await tool.RunAsync("h-abc");

        sessions.CancelCallCount.ShouldBe(1);
        sessions.LastCancelSource.ShouldBe(SubAgentCancelSource.Parent);
        result["status"]!.ToString().ShouldBe("cancelling");
        result["handle"]!.ToString().ShouldBe("h-abc");
    }

    [Fact]
    public async Task RunAsync_AlreadyTerminal_ReturnsCurrentTerminalState()
    {
        var view = MakeView(SubAgentStatus.Completed);
        var sessions = new FakeSessions(view);
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentCancelTool(config);

        var result = await tool.RunAsync("h-abc");

        sessions.CancelCallCount.ShouldBe(0);
        result["status"]!.ToString().ShouldBe("completed");
        result["handle"]!.ToString().ShouldBe("h-abc");
    }

    [Fact]
    public async Task RunAsync_UnknownHandle_ReturnsNotFound()
    {
        var sessions = new FakeSessions(null);
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentCancelTool(config);

        var result = await tool.RunAsync("unknown-handle");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("not_found");
    }

    [Fact]
    public async Task RunAsync_NoSessions_ReturnsUnavailable()
    {
        var config = new FeatureConfig(SubAgentSessions: null);
        var tool = new SubAgentCancelTool(config);

        var result = await tool.RunAsync("h-xyz");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("unavailable");
    }

    private sealed class FakeSessions(SubAgentSessionView? view) : ISubAgentSessions
    {
        public int ActiveCount => 0;
        public int CancelCallCount { get; private set; }
        public SubAgentCancelSource LastCancelSource { get; private set; }

        public string Start(SubAgentDefinition profile, string prompt, bool silent) => "h-new";
        public SubAgentSessionView? Get(string handle) => view;
        public IReadOnlyList<SubAgentSessionView> List() => view is null ? [] : [view];
        public void Cancel(string handle, SubAgentCancelSource source)
        {
            CancelCallCount++;
            LastCancelSource = source;
        }
        public Task<SubAgentWaitResult> WaitAsync(IReadOnlyList<string> handles, SubAgentWaitMode mode,
            TimeSpan timeout, CancellationToken ct) => Task.FromResult(new SubAgentWaitResult([], []));
        public bool Release(string handle) => false;
    }
}
