using Domain.Contracts;
using McpServerScheduling.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerScheduling.Services;

public sealed class ScheduleDispatcherService(
    IScheduleStore store,
    ICronValidator cronValidator,
    ScheduleNotificationEmitter emitter,
    SchedulingSettings settings,
    ILogger<ScheduleDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(settings.DispatchIntervalSeconds);
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

    private async Task DispatchDueAsync(CancellationToken ct)
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

            if (plan.DeleteAfterFire)
            {
                await store.DeleteAsync(schedule.Id, ct);
            }
            else
            {
                await store.UpdateLastRunAsync(schedule.Id, DateTime.UtcNow, plan.NextRunAt, ct);
            }

            await emitter.EmitAsync(plan.Payload, ct);
            logger.LogInformation("Fired schedule {ScheduleId} for agent {AgentId}", schedule.Id, schedule.AgentId);
        }
    }
}