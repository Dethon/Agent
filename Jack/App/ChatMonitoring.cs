using Domain.Monitor;
using Microsoft.Extensions.Hosting;

namespace Jack.App;

public class ChatMonitoring(ChatMonitor monitor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await monitor.Monitor(cancellationToken);
        }
    }
}