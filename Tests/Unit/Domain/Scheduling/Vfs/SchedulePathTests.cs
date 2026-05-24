using Domain.Tools.Scheduling.Vfs;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class SchedulePathTests
{
    [Theory]
    [InlineData("/", ScheduleNodeKind.Root)]
    [InlineData("/jonas", ScheduleNodeKind.AgentDir)]
    [InlineData("/jonas/agent_info.json", ScheduleNodeKind.AgentInfoFile)]
    [InlineData("/jonas/morning-news", ScheduleNodeKind.ScheduleDir)]
    [InlineData("/jonas/morning-news/schedule.json", ScheduleNodeKind.ScheduleFile)]
    [InlineData("/jonas/morning-news/status.json", ScheduleNodeKind.StatusFile)]
    [InlineData("/jonas/morning-news/run_now.sh", ScheduleNodeKind.RunNowFile)]
    [InlineData("/jonas/morning-news/bogus", ScheduleNodeKind.Unknown)]
    public void Parse_ResolvesNodeKinds(string path, ScheduleNodeKind expected)
    {
        var node = SchedulePath.Parse(path);
        node.Kind.ShouldBe(expected);
    }

    [Fact]
    public void Parse_CapturesAgentAndScheduleSegments()
    {
        var node = SchedulePath.Parse("/jonas/morning-news/schedule.json");
        node.AgentId.ShouldBe("jonas");
        node.ScheduleId.ShouldBe("morning-news");
    }
}