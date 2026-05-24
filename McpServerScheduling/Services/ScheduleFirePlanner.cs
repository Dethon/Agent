using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpServerScheduling.Services;

public sealed record FirePlan(ChannelMessageNotification Payload, DateTime? NextRunAt, bool DeleteAfterFire);

public static class ScheduleFirePlanner
{
    public static FirePlan Plan(Schedule schedule, IReadOnlyList<string> defaultDeliverTo, DateTime? nextRun)
    {
        var channels = schedule.DeliverTo is { Count: > 0 } ? schedule.DeliverTo : defaultDeliverTo;
        var replyTo = channels.Select(c => new ReplyTarget(c, null)).ToList();
        var origin = new MessageOrigin("schedule", schedule.Id);

        var payload = ScheduleNotificationEmitter.BuildPayload(
            conversationId: $"sched-{schedule.Id}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            sender: "scheduler",
            content: schedule.Prompt,
            agentId: schedule.AgentId,
            replyTo: replyTo,
            origin: origin);

        var deleteAfterFire = schedule.CronExpression is null;
        return new FirePlan(payload, nextRun, deleteAfterFire);
    }
}