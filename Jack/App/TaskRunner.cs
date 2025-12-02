using Domain.Monitor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jack.App;

public class TaskRunner(TaskQueue queue, ILogger<TaskRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var workItem = await queue.DequeueTask(cancellationToken);
            try
            {
                await workItem(cancellationToken);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError(ex, "Error occurred executing {WorkItem}.", nameof(workItem));
                }
            }
        }
    }
}