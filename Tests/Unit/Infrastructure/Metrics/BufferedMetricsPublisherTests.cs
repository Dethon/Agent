using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class BufferedMetricsPublisherTests
{
    private sealed class FakeSink : IMetricSink
    {
        private readonly List<MetricEvent> _events = [];
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Gate { get; set; }
        public Func<MetricEvent, Exception?> ThrowFor { get; set; } = _ => null;

        public IReadOnlyList<MetricEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task Sent => _sent.Task;

        public async Task SendAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (ThrowFor(metricEvent) is { } ex)
            {
                _sent.TrySetResult();
                throw ex;
            }

            lock (_events)
            {
                _events.Add(metricEvent);
            }

            _sent.TrySetResult();
        }
    }

    private static ErrorEvent Event(string msg = "m") =>
        new() { Service = "test", ErrorType = "t", Message = msg };

    [Fact]
    public async Task Publish_SinkBlocked_ReturnsWithoutWaiting()
    {
        var sink = new FakeSink
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var publisher = new BufferedMetricsPublisher(sink);

        // Publish is void, so "did not wait" is only observable as the sink not having run yet
        // while the gate is still closed.
        publisher.Publish(Event());

        sink.Events.ShouldBeEmpty();
        sink.Gate.TrySetResult();
    }

    [Fact]
    public async Task Publish_SinkThrows_DoesNotEscape()
    {
        var sink = new FakeSink { ThrowFor = _ => new InvalidOperationException("redis down") };
        await using var publisher = new BufferedMetricsPublisher(sink);

        Should.NotThrow(() => publisher.Publish(Event()));

        await sink.Sent;
    }

    [Fact]
    public async Task Publish_SinkThrowsOnOneEvent_LaterEventsStillDrain()
    {
        var sink = new FakeSink
        {
            ThrowFor = e => e is ErrorEvent { Message: "poison" }
                ? new InvalidOperationException("redis down")
                : null
        };
        var publisher = new BufferedMetricsPublisher(sink);

        publisher.Publish(Event("poison"));
        publisher.Publish(Event("after"));

        await publisher.DisposeAsync();

        sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ErrorEvent>().Message.ShouldBe("after");
    }

    [Fact]
    public async Task Publish_BufferFull_DropsWithoutThrowingOrBlocking()
    {
        var sink = new FakeSink
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var publisher = new BufferedMetricsPublisher(sink, capacity: 1);

        // The drain may have taken the first event and parked on the gate, so fill well past
        // capacity: every write beyond it drops rather than throwing or waiting for room.
        Should.NotThrow(() => Enumerable.Range(0, 10).ToList().ForEach(i => publisher.Publish(Event($"e{i}"))));

        sink.Gate.TrySetResult();
    }

    [Fact]
    public async Task DisposeAsync_FlushesPendingEvents()
    {
        var sink = new FakeSink();
        var publisher = new BufferedMetricsPublisher(sink);
        Enumerable.Range(0, 100).ToList().ForEach(i => publisher.Publish(Event($"e{i}")));

        await publisher.DisposeAsync();

        sink.Events.Count.ShouldBe(100);
    }
}