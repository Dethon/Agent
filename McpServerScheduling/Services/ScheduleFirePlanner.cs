using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpServerScheduling.Services;

public sealed record FirePlan(ChannelMessageNotification Payload, DateTime? NextRunAt, bool DeleteAfterFire);

public static class ScheduleFirePlanner
{
    public static FirePlan Plan(Schedule schedule, IReadOnlyList<string> defaultDeliverTo, DateTime? nextRun)
    {
        var channels = schedule.DeliverTo is { Count: > 0 } ? schedule.DeliverTo : defaultDeliverTo;
        // Multiple entries for the same channel (e.g. several `voice:<id>` satellites) are one
        // logical delivery: merge their sub-addresses into a single ReplyTarget so the fan-out
        // produces one conversation spoken on every satellite, not a duplicate per satellite.
        var replyTo = channels
            .Select(ParseTarget)
            .GroupBy(t => t.ChannelId)
            .Select(Coalesce)
            .ToList();
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

    private static ReplyTarget Coalesce(IGrouping<string, ReplyTarget> group)
    {
        var targets = group.ToList();
        if (targets.Count == 1)
        {
            return targets[0];
        }

        // Join the distinct sub-addresses (satellite ids). Bare entries (null address) contribute
        // nothing; if every entry was bare the merged address stays null (= "all" for voice).
        var addresses = targets
            .Select(t => t.Address)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .ToList();
        var merged = addresses.Count == 0 ? null : string.Join(",", addresses);
        return new ReplyTarget(group.Key, null, merged);
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