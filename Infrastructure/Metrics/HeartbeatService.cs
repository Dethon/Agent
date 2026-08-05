using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Metrics;

public sealed class HeartbeatService(IMetricsPublisher publisher, string serviceName) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            publisher.Publish(new HeartbeatEvent { Service = serviceName });

            await Task.Delay(_interval, stoppingToken);
        }
    }
}