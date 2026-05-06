using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Domain.Tools.SubAgents;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentReleaseToolTests
{
    [Fact]
    public async Task RunAsync_TerminalSession_RemovesIt()
    {
        var sessions = new FakeSessions(releaseResult: true);
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentReleaseTool(config);

        var result = await tool.RunAsync("h-done");

        result["status"]!.ToString().ShouldBe("released");
    }

    [Fact]
    public async Task RunAsync_RunningSession_ReturnsInvalidOperation()
    {
        var sessions = new FakeSessions(throwException: new InvalidOperationException("Cannot release a running session"));
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentReleaseTool(config);

        var result = await tool.RunAsync("h-running");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("invalid_operation");
    }

    [Fact]
    public async Task RunAsync_UnknownHandle_ReturnsNotFound()
    {
        var sessions = new FakeSessions(releaseResult: false);
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentReleaseTool(config);

        var result = await tool.RunAsync("h-unknown");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("not_found");
    }

    [Fact]
    public async Task RunAsync_NoSessions_ReturnsUnavailable()
    {
        var config = new FeatureConfig(SubAgentSessions: null);
        var tool = new SubAgentReleaseTool(config);

        var result = await tool.RunAsync("h-xyz");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("unavailable");
    }

    private sealed class FakeSessions(bool releaseResult = false, InvalidOperationException? throwException = null)
        : ISubAgentSessions
    {
        public int ActiveCount => 0;

        public string Start(SubAgentDefinition profile, string prompt, bool silent) => "h-new";
        public SubAgentSessionView? Get(string handle) => null;
        public IReadOnlyList<SubAgentSessionView> List() => [];
        public void Cancel(string handle, SubAgentCancelSource source) { }
        public Task<SubAgentWaitResult> WaitAsync(IReadOnlyList<string> handles, SubAgentWaitMode mode,
            TimeSpan timeout, CancellationToken ct) => Task.FromResult(new SubAgentWaitResult([], []));

        public bool Release(string handle)
        {
            if (throwException is not null) throw throwException;
            return releaseResult;
        }
    }
}
