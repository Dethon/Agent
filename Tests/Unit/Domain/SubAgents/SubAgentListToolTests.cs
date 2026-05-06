using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Domain.Tools.SubAgents;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentListToolTests
{
    [Fact]
    public void RunAsync_NoSessions_ReturnsUnavailable()
    {
        var config = new FeatureConfig(SubAgentSessions: null);
        var tool = new SubAgentListTool(config);

        var result = tool.RunAsync();

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.ToString().ShouldBe("unavailable");
    }

    [Fact]
    public void RunAsync_EmptyList_ReturnsEmptyArray()
    {
        var sessions = new FakeSubAgentSessions { ListFunc = () => [] };
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentListTool(config);

        var result = tool.RunAsync();

        result.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public void RunAsync_ReturnsCompactViewsForAllSessions()
    {
        var v1 = new SubAgentSessionView
        {
            Handle = "h-1",
            SubAgentId = "researcher",
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            ElapsedSeconds = 5.0,
            Turns = []
        };
        var v2 = new SubAgentSessionView
        {
            Handle = "h-2",
            SubAgentId = "scraper",
            Status = SubAgentStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            ElapsedSeconds = 30.0,
            Turns = [],
            Result = "done"
        };
        var sessions = new FakeSubAgentSessions { ListFunc = () => [v1, v2] };
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentListTool(config);

        var result = tool.RunAsync();

        var array = result.AsArray();
        array.Count.ShouldBe(2);

        array[0]!["handle"]!.ToString().ShouldBe("h-1");
        array[0]!["subagent_id"]!.ToString().ShouldBe("researcher");
        array[0]!["status"]!.ToString().ShouldBe("running");
        array[0]!["elapsed_seconds"].ShouldNotBeNull();
        array[0]!["started_at"].ShouldNotBeNull();

        array[1]!["handle"]!.ToString().ShouldBe("h-2");
        array[1]!["status"]!.ToString().ShouldBe("completed");
        array[1]!["subagent_id"]!.ToString().ShouldBe("scraper");
        array[1]!["elapsed_seconds"].ShouldNotBeNull();
        array[1]!["started_at"].ShouldNotBeNull();
    }
}
