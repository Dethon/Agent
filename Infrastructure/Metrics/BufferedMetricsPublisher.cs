using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Metrics;

public sealed class BufferedMetricsPublisher : IMetricsPublisher, IAsyncDisposable
{
    private readonly IMetricsPublisher _inner;
    private readonly ILogger<BufferedMetricsPublisher>? _logger;
    private readonly Channel<MetricEvent> _events;
    private readonly Task _drainTask;
    private int _disposed;

    public BufferedMetricsPublisher(
        IMetricsPublisher inner,
        ILogger<BufferedMetricsPublisher>? logger = null,
        int capacity = 10_000)
    {
        _inner = inner;
        _logger = logger;
        _events = Channel.CreateBounded<MetricEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _drainTask = Task.Run(DrainAsync);
    }

    public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            return Task.CompletedTask;
        }

        if (!_events.Writer.TryWrite(metricEvent))
        {
            _logger?.LogWarning("Metrics buffer full; dropping {EventType}", metricEvent.GetType().Name);
        }

        return Task.CompletedTask;
    }

    private async Task DrainAsync()
    {
        await foreach (var metricEvent in _events.Reader.ReadAllAsync())
        {
            try
            {
                await _inner.PublishAsync(metricEvent);
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