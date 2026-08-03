using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Metrics;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Metrics;

public class LatencyScopeTests
{
    private sealed class RecordingPublisher : IMetricsPublisher
    {
        public readonly List<MetricEvent> Events = [];

        public void Publish(MetricEvent metricEvent) => Events.Add(metricEvent);
    }

    private readonly RecordingPublisher _publisher = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    [Fact]
    public void Dispose_MeasuredBlockReturns_PublishesOneLatencyEvent()
    {
        using (_publisher.MeasureLatency(LatencyStage.ToolExec, "conv-1", "agent-1", time: _clock))
        {
            _clock.Advance(TimeSpan.FromMilliseconds(120));
        }

        var latency = _publisher.Events.ShouldHaveSingleItem().ShouldBeOfType<LatencyEvent>();
        latency.Stage.ShouldBe(LatencyStage.ToolExec);
        latency.ConversationId.ShouldBe("conv-1");
        latency.AgentId.ShouldBe("agent-1");
        latency.DurationMs.ShouldBe(120);
    }

    // The duplicated per-branch emission this replaces existed because a tool call has to be
    // measured whether it returned or threw. One scope covers both paths.
    [Fact]
    public void Dispose_MeasuredBlockThrows_PublishesAndLetsTheExceptionOut()
    {
        var thrown = Should.Throw<InvalidOperationException>(() =>
        {
            using (_publisher.MeasureLatency(LatencyStage.ToolExec, "conv-1", time: _clock))
            {
                _clock.Advance(TimeSpan.FromMilliseconds(40));
                throw new InvalidOperationException("tool blew up");
            }
        });

        thrown.Message.ShouldBe("tool blew up");
        var latency = _publisher.Events.ShouldHaveSingleItem().ShouldBeOfType<LatencyEvent>();
        latency.Stage.ShouldBe(LatencyStage.ToolExec);
        latency.DurationMs.ShouldBe(40);
    }

    // Four sites emit a domain-specific event carrying the same duration as their latency event.
    // Reading it off the scope is what lets them drop their second stopwatch.
    [Fact]
    public void ElapsedMilliseconds_ReadBeforeDispose_MatchesThePublishedDuration()
    {
        long observed;

        using (var scope = _publisher.MeasureLatency(LatencyStage.MemoryRecall, "conv-1", time: _clock))
        {
            _clock.Advance(TimeSpan.FromMilliseconds(75));
            observed = scope.ElapsedMilliseconds;
        }

        observed.ShouldBe(75);
        _publisher.Events.ShouldHaveSingleItem().ShouldBeOfType<LatencyEvent>().DurationMs.ShouldBe(observed);
    }

    [Fact]
    public void Dispose_CalledTwice_PublishesOnce()
    {
        var scope = _publisher.MeasureLatency(
            LatencyStage.LlmTotal, "conv-1", model: "anthropic/claude", time: _clock);

        scope.Dispose();
        scope.Dispose();

        _publisher.Events.ShouldHaveSingleItem().ShouldBeOfType<LatencyEvent>()
            .Model.ShouldBe("anthropic/claude");
    }
}