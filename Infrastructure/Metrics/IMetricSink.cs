using Domain.DTOs.Metrics;

namespace Infrastructure.Metrics;

// The transport a metrics publisher drains into. Sending is a real network operation and is
// allowed to throw — the buffered publisher owns what a failure means, so an adapter never has
// to swallow its own errors. Lives here rather than Domain/Contracts because Domain never
// consumes a sink; only Infrastructure implements or drains one.
public interface IMetricSink
{
    Task SendAsync(MetricEvent metricEvent, CancellationToken ct = default);
}