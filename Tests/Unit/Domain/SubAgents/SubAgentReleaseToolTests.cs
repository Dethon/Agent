using Domain.DTOs;
using Domain.Tools.SubAgents;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentReleaseToolTests
{
    [Fact]
    public async Task RunAsync_TerminalSession_RemovesIt()
    {
        var sessions = new FakeSubAgentSessions { ReleaseFunc = _ => true };
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentReleaseTool(config);

        var result = await tool.RunAsync("h-done");

        result["status"]!.ToString().ShouldBe("released");
    }

    [Fact]
    public async Task RunAsync_RunningSession_ReturnsInvalidOperation()
    {
        var sessions = new FakeSubAgentSessions
        {
            ReleaseFunc = _ => throw new InvalidOperationException("Cannot release a running session")
        };
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentReleaseTool(config);

        var result = await tool.RunAsync("h-running");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("invalid_operation");
    }

    [Fact]
    public async Task RunAsync_UnknownHandle_ReturnsNotFound()
    {
        var sessions = new FakeSubAgentSessions { ReleaseFunc = _ => false };
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
}
