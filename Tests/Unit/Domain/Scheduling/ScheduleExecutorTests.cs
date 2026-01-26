using Domain.Contracts;
using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleExecutorTests
{
    [Fact]
    public void IChatMessengerClient_HasSupportsScheduledNotificationsProperty()
    {
        // Verify the capability pattern is implemented correctly in the interface
        var property = typeof(IChatMessengerClient).GetProperty("SupportsScheduledNotifications");

        property.ShouldNotBeNull();
        property.PropertyType.ShouldBe(typeof(bool));
        property.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void Schedule_UsesUserIdDirectly()
    {
        var schedule = new Schedule
        {
            Id = "sched_test",
            Agent = new AgentDefinition
            {
                Id = "jack",
                Name = "Jack",
                Model = "test",
                McpServerEndpoints = []
            },
            Prompt = "Test prompt",
            CronExpression = "0 9 * * *",
            UserId = "user123",
            CreatedAt = DateTime.UtcNow,
            NextRunAt = DateTime.UtcNow.AddHours(1)
        };

        schedule.UserId.ShouldBe("user123");
        typeof(Schedule).GetProperty("Target").ShouldBeNull();
    }
}
