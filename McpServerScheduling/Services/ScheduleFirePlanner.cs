using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpServerScheduling.Services;

public sealed record FirePlan(ChannelMessageNotification Payload, DateTime? NextRunAt, bool DeleteAfterFire);

public static class ScheduleFirePlanner
{
    public static FirePlan Plan(Schedule schedule, IReadOnlyList<string> defaultDeliverTo, DateTime? nextRun)
    {
        var channels = schedule.DeliverTo is { Count: > 0 } ? schedule.DeliverTo : defaultDeliverTo;
        var replyTo = channels.Select(ParseTarget).ToList();
        var origin = new MessageOrigin(MessageOriginKind.Schedule, schedule.Id);

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

    private static ReplyTarget ParseTarget(string entry)
    {
        var separator = entry.IndexOf(':');
        if (separator < 0)
        {
            return new ReplyTarget(entry, null);
        }

        var channelId = entry[..separator];
        var address = entry[(separator + 1)..];
        return new ReplyTarget(channelId, null, string.IsNullOrWhiteSpace(address) ? null : address);
    }
}