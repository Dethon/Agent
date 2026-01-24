using System.Threading.Channels;
using Domain.DTOs;
using Domain.Monitor;

namespace Agent.App;

public class ScheduleMonitoring(
    ScheduleDispatcher dispatcher,
    ScheduleExecutor executor,
    Channel<Schedule> scheduleChannel) : BackgroundService
{
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var dispatchTask = RunDispatcherAsync(ct);
        var executeTask = executor.ProcessSchedulesAsync(ct);

        await Task.WhenAll(dispatchTask, executeTask);
    }

    private async Task RunDispatcherAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await dispatcher.DispatchDueSchedulesAsync(ct);
                await Task.Delay(DispatchInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        finally
        {
            scheduleChannel.Writer.Complete();
        }
    }
}
