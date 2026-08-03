using Domain.DTOs.Metrics;

namespace Domain.Contracts;

// The fire-and-forget thing a caller holds. Publishing cannot fail, cannot block and cannot be
// observed, so no call site has to decide what a failed publish means. The transport that really
// can fail is Infrastructure's IMetricSink, behind BufferedMetricsPublisher.
//
// The absence of a Task here is deliberate and is not an oversight to be fixed:
// docs/adr/0002-metrics-publishing-is-fire-and-forget.md.
public interface IMetricsPublisher
{
    void Publish(MetricEvent metricEvent);
}