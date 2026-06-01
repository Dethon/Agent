using Domain.Tools.Printing;
using McpServerPrinter.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerPrinter.Services;

// Periodically submits documents whose writes have gone quiet and prunes finished jobs.
public sealed class PrintSubmissionWorker(
    PrintQueueCoordinator coordinator,
    PrinterSettings settings,
    ILogger<PrintSubmissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.TickIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await coordinator.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Print queue tick failed");
            }
        }
    }
}