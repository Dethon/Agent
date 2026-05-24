using Domain.DTOs;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.DTOs;

public class ScheduleDtoTests
{
    [Fact]
    public void Schedule_WithAgentIdAndDeliverTo_ExposesValues()
    {
        var s = new Schedule
        {
            Id = "morning-news",
            AgentId = "jonas",
            Prompt = "summarize news",
            CronExpression = "0 8 * * *",
            DeliverTo = ["signalr", "telegram"],
            CreatedAt = DateTime.UtcNow
        };

        s.AgentId.ShouldBe("jonas");
        s.DeliverTo!.Count.ShouldBe(2);
    }
}