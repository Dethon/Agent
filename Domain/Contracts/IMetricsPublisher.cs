using Domain.DTOs.Metrics;

namespace Domain.Contracts;

public interface IMetricsPublisher
{
    Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default);
}
