using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Metrics;
using Shouldly;

namespace Tests.Unit.Domain.Metrics;

public class LatencyScopeTests
{
    private sealed class RecordingPublisher : IMetricsPublisher
    {
        public readonly List<MetricEvent> Events = [];

        public void Publish(MetricEvent metricEvent) => Events.Add(metricEvent);

        public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            Publish(metricEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Dispose_MeasuredBlockReturns_PublishesOneLatencyEvent()
    {
        var publisher = new RecordingPublisher();

        using (publisher.MeasureLatency(LatencyStage.ToolExec, "conv-1", "agent-1"))
        {
            Thread.Sleep(5);
        }

        var latency = publisher.Events.ShouldHaveSingleItem().ShouldBeOfType<LatencyEvent>();
        latency.Stage.ShouldBe(LatencyStage.ToolExec);
        latency.ConversationId.ShouldBe("conv-1");
        latency.AgentId.ShouldBe("agent-1");
        latency.DurationMs.ShouldBeGreaterThanOrEqualTo(5);
    }

    // The duplicated per-branch emission this replaces existed because a tool call has to be
    // measured whether it returned or threw. One scope covers both paths.
    [Fact]
    public void Dispose_MeasuredBlockThrows_PublishesAndLetsTheExceptionOut()
    {
        var publisher = new RecordingPublisher();

        var thrown = Should.Throw<InvalidOperationException>(() =>
        {
            using (publisher.MeasureLatency(LatencyStage.ToolExec, "conv-1"))
            {
                throw new InvalidOperationException("tool blew up");
            }
        });

        thrown.Message.ShouldBe("tool blew up");
        publisher.Events.ShouldHaveSingleItem().ShouldBeOfType<LatencyEvent>()
            .Stage.ShouldBe(LatencyStage.ToolExec);
    }

    // Four sites emit a domain-specific event carrying the same duration as their latency event.
    // Reading it off the scope is what lets them drop their second stopwatch.
    [Fact]
    public void ElapsedMilliseconds_ReadBeforeDispose_MatchesThePublishedDuration()
    {
        var publisher = new RecordingPublisher();
        long observed;

        using (var scope = publisher.MeasureLatency(LatencyStage.MemoryRecall, "conv-1"))
        {
            Thread.Sleep(10);
            observed = scope.ElapsedMilliseconds;
        }

        var published = publisher.Events.ShouldHaveSingleItem().ShouldBeOfType<LatencyEvent>().DurationMs;
        observed.ShouldBeGreaterThanOrEqualTo(10);
        published.ShouldBeGreaterThanOrEqualTo(observed);
        (published - observed).ShouldBeLessThan(100);
    }

    [Fact]
    public void Dispose_CalledTwice_PublishesOnce()
    {
        var publisher = new RecordingPublisher();
        var scope = publisher.MeasureLatency(LatencyStage.LlmTotal, "conv-1", model: "anthropic/claude");

        scope.Dispose();
        scope.Dispose();

        publisher.Events.ShouldHaveSingleItem().ShouldBeOfType<LatencyEvent>()
            .Model.ShouldBe("anthropic/claude");
    }
}