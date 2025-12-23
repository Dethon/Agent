using Domain.Monitor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agent.App;

public class CleanupMonitoring(AgentCleanupMonitor monitor, ILogger<CleanupMonitoring> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var timespan = TimeSpan.FromSeconds(60);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await monitor.Check(cancellationToken);
                await Task.Delay(timespan, cancellationToken);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError(ex, "Cleanup monitor exception: {exceptionMessage}", ex.Message);
                }
            }
        }
    }
}