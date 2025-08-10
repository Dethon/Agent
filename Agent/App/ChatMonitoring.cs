using Domain.Monitor;
using Microsoft.Extensions.Hosting;

namespace Agent.App;

public class ChatMonitoring(ChatMonitor monitor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await monitor.Monitor(cancellationToken);
    }
}