using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Metrics;

// The only metrics publisher a host registers. Publishing is a channel write, so it cannot fail
// and cannot block; the sink round trip happens on the drain reader, where a failure is logged
// and the loop carries on.
public sealed class BufferedMetricsPublisher : IMetricsPublisher, IAsyncDisposable
{
    private readonly IMetricSink _sink;
    private readonly ILogger<BufferedMetricsPublisher>? _logger;
    private readonly Channel<MetricEvent> _events;
    private readonly Task _drainTask;
    private int _disposed;

    public BufferedMetricsPublisher(
        IMetricSink sink,
        ILogger<BufferedMetricsPublisher>? logger = null,
        int capacity = 10_000)
    {
        _sink = sink;
        _logger = logger;
        _events = Channel.CreateBounded<MetricEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _drainTask = Task.Run(DrainAsync);
    }

    public void Publish(MetricEvent metricEvent)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_events.Writer.TryWrite(metricEvent))
        {
            _logger?.LogWarning("Metrics buffer full; dropping {EventType}", metricEvent.GetType().Name);
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var metricEvent in _events.Reader.ReadAllAsync())
        {
            try
            {
                await _sink.SendAsync(metricEvent);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to publish {EventType}", metricEvent.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _events.Writer.TryComplete();
        await Task.WhenAny(_drainTask, Task.Delay(TimeSpan.FromSeconds(5)));
    }
}