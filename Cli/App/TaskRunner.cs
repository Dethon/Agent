using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cli.App;

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
                logger.LogError(ex, "Error occurred executing {WorkItem}.", nameof(workItem));
            }
        }
    }

    
}