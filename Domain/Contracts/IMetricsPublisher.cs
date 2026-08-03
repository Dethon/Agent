using Domain.DTOs.Metrics;

namespace Domain.Contracts;

// The fire-and-forget thing a caller holds. Publishing cannot fail, cannot block and cannot be
// observed, so no call site has to decide what a failed publish means. The transport that really
// can fail is Infrastructure's IMetricSink, behind BufferedMetricsPublisher.
// See docs/adr/0002-metrics-publishing-is-fire-and-forget.md.
public interface IMetricsPublisher
{
    void Publish(MetricEvent metricEvent) => _ = PublishAsync(metricEvent);

    // Expand half of an expand-contract migration. Callers move to Publish a batch at a time; this
    // method and its bridging default above both go away once none are left.
    Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default);
}