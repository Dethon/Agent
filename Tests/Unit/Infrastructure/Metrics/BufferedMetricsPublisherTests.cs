using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class BufferedMetricsPublisherTests
{
    private sealed class RecordingPublisher : IMetricsPublisher
    {
        private readonly List<MetricEvent> _events = [];
        public TaskCompletionSource? Gate { get; set; }
        public Exception? ToThrow { get; set; }

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

        public async Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (ToThrow is not null)
            {
                throw ToThrow;
            }

            lock (_events)
            {
                _events.Add(metricEvent);
            }
        }
    }

    private static ErrorEvent Event(string msg = "m") =>
        new() { Service = "test", ErrorType = "t", Message = msg };

    [Fact]
    public async Task PublishAsync_InnerBlocked_ReturnsImmediately()
    {
        var inner = new RecordingPublisher
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var publisher = new BufferedMetricsPublisher(inner);

        var publish = publisher.PublishAsync(Event());

        publish.IsCompleted.ShouldBeTrue("hot-path publish must not wait on the inner publisher");
        inner.Gate.TrySetResult();
    }

    [Fact]
    public async Task PublishAsync_InnerThrows_DoesNotPropagate()
    {
        var inner = new RecordingPublisher { ToThrow = new InvalidOperationException("redis down") };
        await using var publisher = new BufferedMetricsPublisher(inner);

        await Should.NotThrowAsync(() => publisher.PublishAsync(Event()));
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_BufferFull_DropsWithoutThrowing()
    {
        var inner = new RecordingPublisher
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var publisher = new BufferedMetricsPublisher(inner, capacity: 1);

        // First event may be consumed by the drain (blocked on the gate); fill until TryWrite drops.
        foreach (var i in Enumerable.Range(0, 10))
        {
            await Should.NotThrowAsync(() => publisher.PublishAsync(Event($"e{i}")));
        }

        inner.Gate.TrySetResult();
    }

    [Fact]
    public async Task DisposeAsync_FlushesPendingEvents()
    {
        var inner = new RecordingPublisher();
        var publisher = new BufferedMetricsPublisher(inner);
        foreach (var i in Enumerable.Range(0, 100))
        {
            await publisher.PublishAsync(Event($"e{i}"));
        }

        await publisher.DisposeAsync();

        inner.Events.Count.ShouldBe(100);
    }
}