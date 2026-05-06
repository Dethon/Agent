using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Domain.Tools.SubAgents;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentWaitToolTests
{
    [Fact]
    public async Task RunAsync_AnyMode_DelegatesToSessions()
    {
        SubAgentWaitMode? capturedMode = null;
        TimeSpan? capturedTimeout = null;
        var sessions = new FakeSubAgentSessions
        {
            WaitFunc = (_, mode, timeout, _) =>
            {
                capturedMode = mode;
                capturedTimeout = timeout;
                return Task.FromResult(new SubAgentWaitResult(["h1"], ["h2"]));
            }
        };
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentWaitTool(config);

        var result = await tool.RunAsync(["h1", "h2"], mode: "any", timeout_seconds: 30);

        capturedMode.ShouldBe(SubAgentWaitMode.Any);
        capturedTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        result["completed"]!.AsArray().Select(n => n!.ToString()).ShouldBe(["h1"]);
        result["still_running"]!.AsArray().Select(n => n!.ToString()).ShouldBe(["h2"]);
    }

    [Fact]
    public async Task RunAsync_AllMode_DelegatesWithCorrectMode()
    {
        SubAgentWaitMode? capturedMode = null;
        var sessions = new FakeSubAgentSessions
        {
            WaitFunc = (_, mode, _, _) =>
            {
                capturedMode = mode;
                return Task.FromResult(new SubAgentWaitResult(["h1", "h2"], []));
            }
        };
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentWaitTool(config);

        var result = await tool.RunAsync(["h1", "h2"], mode: "all", timeout_seconds: 60);

        capturedMode.ShouldBe(SubAgentWaitMode.All);
        result["completed"]!.AsArray().Select(n => n!.ToString()).ShouldBe(["h1", "h2"]);
        result["still_running"]!.AsArray().Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task RunAsync_InvalidTimeout_ReturnsInvalidArgument(int timeoutSeconds)
    {
        var sessions = new FakeSubAgentSessions();
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentWaitTool(config);

        var result = await tool.RunAsync(["h1"], timeout_seconds: timeoutSeconds);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task RunAsync_InvalidMode_ReturnsInvalidArgument()
    {
        var sessions = new FakeSubAgentSessions();
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentWaitTool(config);

        var result = await tool.RunAsync(["h1"], mode: "bogus");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task RunAsync_NoSessions_ReturnsUnavailable()
    {
        var config = new FeatureConfig(SubAgentSessions: null);
        var tool = new SubAgentWaitTool(config);

        var result = await tool.RunAsync(["h1"]);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("unavailable");
    }
}
