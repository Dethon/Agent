using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ScheduleDispatcher(
    IScheduleStore store,
    ICronValidator cronValidator,
    Channel<Schedule> scheduleChannel,
    ILogger<ScheduleDispatcher> logger)
{
    public async Task DispatchDueSchedulesAsync(CancellationToken ct)
    {
        try
        {
            var dueSchedules = await store.GetDueSchedulesAsync(DateTime.UtcNow, ct);

            foreach (var schedule in dueSchedules)
            {
                var nextRun = CalculateNextRun(schedule);
                await store.UpdateLastRunAsync(schedule.Id, DateTime.UtcNow, nextRun, ct);

                await scheduleChannel.Writer.WriteAsync(schedule, ct);

                logger.LogInformation(
                    "Dispatched schedule {ScheduleId} for agent {AgentName}",
                    schedule.Id,
                    schedule.Agent.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error dispatching due schedules");
        }
    }

    private DateTime? CalculateNextRun(Schedule schedule)
    {
        if (schedule.CronExpression is null)
        {
            return null;
        }

        return cronValidator.GetNextOccurrence(schedule.CronExpression, DateTime.UtcNow);
    }
}
