using Domain.Contracts;
using Domain.DTOs.Metrics;

namespace Infrastructure.Metrics;

public sealed class AgentMetricsPublisher(IMetricsPublisher inner, string agentId) : IMetricsPublisher
{
    public void Publish(MetricEvent metricEvent) =>
        inner.Publish(metricEvent with { AgentId = agentId });
}