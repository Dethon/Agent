using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Domain.Metrics;

// Measuring a span is a scope, not a stopwatch triple. Disposal covers the return path and the
// throw path with one statement, so a site cannot measure one branch and forget the other.
//
// It publishes on disposal, including an early return, so open it after any guard that can return
// before the measured work begins.
public sealed class LatencyScope : IDisposable
{
    private readonly IMetricsPublisher _publisher;
    private readonly LatencyStage _stage;
    private readonly string? _conversationId;
    private readonly string? _agentId;
    private readonly string? _model;
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private int _published;

    internal LatencyScope(
        IMetricsPublisher publisher,
        LatencyStage stage,
        string? conversationId,
        string? agentId,
        string? model,
        TimeProvider time)
    {
        _publisher = publisher;
        _stage = stage;
        _conversationId = conversationId;
        _agentId = agentId;
        _model = model;
        _time = time;
        _startedAt = time.GetTimestamp();
    }

    // Read this where a domain-specific event has to carry the same duration as the latency event,
    // instead of running a second stopwatch alongside.
    public long ElapsedMilliseconds => (long)_time.GetElapsedTime(_startedAt).TotalMilliseconds;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _published, 1) != 0)
        {
            return;
        }

        _publisher.Publish(new LatencyEvent
        {
            Stage = _stage,
            DurationMs = ElapsedMilliseconds,
            Model = _model,
            ConversationId = _conversationId,
            AgentId = _agentId
        });
    }
}

public static class LatencyScopeExtensions
{
    public static LatencyScope MeasureLatency(
        this IMetricsPublisher publisher,
        LatencyStage stage,
        string? conversationId = null,
        string? agentId = null,
        string? model = null,
        TimeProvider? time = null) =>
        new(publisher, stage, conversationId, agentId, model, time ?? TimeProvider.System);
}