using Domain.DTOs;
using McpServerScheduling.Services;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleFirePlannerTests
{
    [Fact]
    public void Plan_RecurringSchedule_AdvancesNextRunAndDoesNotDelete()
    {
        var s = new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc));

        plan.DeleteAfterFire.ShouldBeFalse();
        plan.NextRunAt.ShouldBe(new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc));
        plan.Payload.AgentId.ShouldBe("jonas");
        plan.Payload.ReplyTo[0].ChannelId.ShouldBe("signalr");
        plan.Payload.Origin.Kind.ShouldBe("schedule");
        plan.Payload.Origin.ScheduleId.ShouldBe("n");
    }

    [Fact]
    public void Plan_OneShotSchedule_DeletesAndUsesScheduleDeliverTo()
    {
        var s = new Schedule { Id = "once", AgentId = "jack", Prompt = "p", RunAt = DateTime.UtcNow, DeliverTo = ["telegram"], CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null);

        plan.DeleteAfterFire.ShouldBeTrue();
        plan.NextRunAt.ShouldBeNull();
        plan.Payload.ReplyTo[0].ChannelId.ShouldBe("telegram");
    }
}