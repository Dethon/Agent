using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Metrics;

// The only metrics publisher a host registers. Publishing is a channel write, so it cannot fail
// and cannot block; the sink round trip happens on the drain reader, where a failure is logged
// and the loop carries on. Every way the drain can stop delivering — a full buffer, a reader that
// died, a disposal that ran out of patience — says so in the log, because publishing keeps
// succeeding either way and there is nothing else for an operator to go on.
public sealed class BufferedMetricsPublisher : IMetricsPublisher, IAsyncDisposable
{
    private readonly IMetricSink _sink;
    private readonly ILogger<BufferedMetricsPublisher>? _logger;
    private readonly Channel<MetricEvent> _events;
    private readonly Task _drainTask;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public BufferedMetricsPublisher(
        IMetricSink sink,
        ILogger<BufferedMetricsPublisher>? logger = null,
        int capacity = 10_000,
        TimeProvider? timeProvider = null)
    {
        _sink = sink;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Wait, not DropWrite, even though the intent is to drop: under DropWrite, TryWrite discards
        // the event and still returns true, so the warning below was unreachable and the one
        // irrecoverable loss was silent. Under Wait, TryWrite refuses instead of blocking — the same
        // event is dropped, and the caller finds out.
        _events = Channel.CreateBounded<MetricEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
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
        try
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
        catch (Exception ex)
        {
            // The reader is the only thing that empties the channel, and nothing restarts it, so a
            // throw from anywhere but the sink round trip leaves Publish writing into a buffer
            // nobody reads. Whatever it was, this is the only trace it can leave.
            Log(logger => logger.LogError(ex, "Metrics drain stopped; no further events reach the sink"));
        }
    }

    // The drain's own logging can be what threw, and a second throw on the way out would be the
    // silence this exists to prevent.
    private void Log(Action<ILogger<BufferedMetricsPublisher>> write)
    {
        if (_logger is not { } logger)
        {
            return;
        }

        try
        {
            write(logger);
        }
        catch
        {
            // Nothing left to report it to.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _events.Writer.TryComplete();
        await Task.WhenAny(_drainTask, Task.Delay(TimeSpan.FromSeconds(5), _timeProvider));

        // Walking away from a drain that has not finished loses whatever is still buffered, which
        // nothing downstream can recover — the same irrecoverable loss as dropping at capacity, and
        // it used to be silent.
        if (!_drainTask.IsCompleted)
        {
            Log(logger => logger.LogWarning(
                "Metrics drain abandoned after 5s; {LostCount} buffered events lost",
                _events.Reader.Count));
        }
    }
}