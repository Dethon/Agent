using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class HeartbeatServiceTests
{
    private sealed class RecordingPublisher : IMetricsPublisher
    {
        private readonly List<MetricEvent> _events = [];

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

        public void Publish(MetricEvent metricEvent)
        {
            lock (_events)
            {
                _events.Add(metricEvent);
            }
        }

        public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            Publish(metricEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExecuteAsync_publishes_heartbeat_event_with_service_name()
    {
        var publisher = new RecordingPublisher();
        var sut = new HeartbeatService(publisher, "test-service");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await sut.StartAsync(cts.Token);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        publisher.Events.OfType<HeartbeatEvent>()
            .ShouldContain(e => e.Service == "test-service");
    }
}