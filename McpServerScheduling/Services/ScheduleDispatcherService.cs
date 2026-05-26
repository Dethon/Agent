using Domain.Contracts;
using McpServerScheduling.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerScheduling.Services;

public sealed class ScheduleDispatcherService(
    IScheduleStore store,
    ICronValidator cronValidator,
    IScheduleNotificationEmitter emitter,
    SchedulingSettings settings,
    ILogger<ScheduleDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = ResolveInterval(settings.DispatchIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DispatchDueAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error dispatching due schedules");
            }

            try
            { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Guard against a 0/negative config value: 0 makes Task.Delay return immediately (tight loop)
    // and a negative span throws, so floor the poll interval at one second.
    internal static TimeSpan ResolveInterval(int dispatchIntervalSeconds) =>
        TimeSpan.FromSeconds(Math.Max(1, dispatchIntervalSeconds));

    internal async Task DispatchDueAsync(CancellationToken ct)
    {
        if (!emitter.HasActiveSessions)
        {
            return;
        }

        var due = await store.GetDueSchedulesAsync(DateTime.UtcNow, ct);
        foreach (var schedule in due)
        {
            var nextRun = schedule.CronExpression is null
                ? null
                : cronValidator.GetNextOccurrence(schedule.CronExpression, DateTime.UtcNow);

            var plan = ScheduleFirePlanner.Plan(schedule, settings.DefaultDeliverTo, nextRun);

            // Emit before mutating the store: if no active session receives the
            // notification, leave the schedule due so the next tick retries instead of
            // silently dropping the fire. (Trade-off: a store write that fails after a
            // successful emit can double-fire — at-least-once is the safer default here.)
            if (!await emitter.EmitAsync(plan.Payload, ct))
            {
                logger.LogWarning(
                    "No active session received schedule {ScheduleId}; leaving it due for retry", schedule.Id);
                continue;
            }

            if (plan.DeleteAfterFire)
            {
                await store.DeleteAsync(schedule.Id, ct);
            }
            else
            {
                await store.UpdateLastRunAsync(schedule.Id, DateTime.UtcNow, plan.NextRunAt, ct);
            }

            logger.LogInformation("Fired schedule {ScheduleId} for agent {AgentId}", schedule.Id, schedule.AgentId);
        }
    }
}