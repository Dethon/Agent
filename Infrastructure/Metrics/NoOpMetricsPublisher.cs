using Domain.Contracts;
using Domain.DTOs.Metrics;

namespace Infrastructure.Metrics;

// What an optional publisher parameter coalesces to, so a type stores a non-nullable publisher
// and no site has to null-check before publishing.
public sealed class NoOpMetricsPublisher : IMetricsPublisher
{
    public static readonly NoOpMetricsPublisher Instance = new();

    private NoOpMetricsPublisher()
    {
    }

    public void Publish(MetricEvent metricEvent)
    {
    }

    public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default) => Task.CompletedTask;
}