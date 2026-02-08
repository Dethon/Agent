using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleDtoTests
{
    [Fact]
    public void Schedule_HasUserIdProperty_NotTarget()
    {
        var schedule = new Schedule
        {
            Id = "test",
            Agent = new AgentDefinition
            {
                Id = "agent",
                Name = "Agent",
                Model = "model",
                McpServerEndpoints = []
            },
            Prompt = "test prompt",
            CronExpression = "0 9 * * *",
            CreatedAt = DateTime.UtcNow,
            UserId = "user123"
        };

        schedule.UserId.ShouldBe("user123");

        // Verify Target property no longer exists (compile-time check)
        typeof(Schedule).GetProperty("Target").ShouldBeNull();
    }

    [Fact]
    public void ScheduleSummary_HasUserIdProperty_NotChannel()
    {
        var summary = new ScheduleSummary(
            Id: "test",
            AgentName: "Agent",
            Prompt: "prompt",
            CronExpression: "0 9 * * *",
            RunAt: null,
            NextRunAt: DateTime.UtcNow,
            UserId: "user123");

        summary.UserId.ShouldBe("user123");

        // Verify Channel parameter no longer exists
        typeof(ScheduleSummary).GetProperty("Channel").ShouldBeNull();
    }
}
