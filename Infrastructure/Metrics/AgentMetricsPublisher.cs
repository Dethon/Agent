using Domain.Contracts;
using Domain.DTOs.Metrics;

namespace Infrastructure.Metrics;

public sealed class AgentMetricsPublisher(IMetricsPublisher inner, string agentId) : IMetricsPublisher
{
    public void Publish(MetricEvent metricEvent) =>
        inner.Publish(metricEvent with { AgentId = agentId });

    public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default) =>
        inner.PublishAsync(metricEvent with { AgentId = agentId }, ct);
}