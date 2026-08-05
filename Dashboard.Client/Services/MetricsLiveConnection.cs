using Dashboard.Client.Contracts;
using Dashboard.Client.Effects;
using Dashboard.Client.State.Connection;
using Microsoft.Extensions.Logging;

namespace Dashboard.Client.Services;

// What owns being live. Becoming live is one ordered sequence here — bind, start until it succeeds,
// publish the status — rather than a run of statements inside an effect whose real job is turning
// events into store updates.
public sealed class MetricsLiveConnection(
    IMetricsHubConnection hub,
    MetricsHubBinder binder,
    ConnectionStore connectionStore,
    IMetricsCatchUp catchUp,
    TimeProvider timeProvider,
    ILogger<MetricsLiveConnection> logger) : IAsyncDisposable
{
    private Task? _connecting;
    private bool _started;
    private bool _disposed;

    public Task ConnectAsync() => _started ? Task.CompletedTask : _connecting ??= BecomeLiveAsync();

    private async Task BecomeLiveAsync()
    {
        // Bind once, before the first start attempt. A failed start leaves the hub connection and
        // its registrations intact, so rebinding per attempt would double every handler, and a
        // reconnect reuses the same hub connection, so nothing needs rebinding there either.
        binder.Bind(hub);

        // Closed is not handled: with a retry policy that never gives up, the transport only closes
        // for good when this module disposes it.
        hub.Reconnecting += _ =>
        {
            connectionStore.SetReconnecting();
            return Task.CompletedTask;
        };

        hub.Reconnected += _ => BecomeLiveAndCatchUpAsync();

        connectionStore.SetConnecting();
        if (!await StartUntilItSucceedsAsync())
        {
            return;
        }

        // The latch records a start that succeeded, not one that was attempted: setting it before
        // the work is what used to leave a failed first start believing it was already running.
        _started = true;
        await BecomeLiveAndCatchUpAsync();
    }

    // The last two steps of becoming live, shared with the path the transport takes when it
    // reconnects on its own — the path that used to do nothing but flip a flag.
    private async Task BecomeLiveAndCatchUpAsync()
    {
        connectionStore.SetLive();

        // Never on the first connection: ordinary page load fetches the same data there, and
        // catching up as well would double every request on first paint.
        if (connectionStore.State.Epoch <= 1)
        {
            return;
        }

        // Pushes are held while catch-up replaces the event lists, then released against what the
        // reload delivered: without the hold, an older snapshot erases a push that arrived first,
        // and a push the snapshot already contains lands twice.
        binder.HoldPushes();
        try
        {
            await catchUp.CatchUpAsync();
        }
        catch (Exception exception)
        {
            // The connection is live whether or not the reload worked, and every family keeps the
            // values it already had.
            logger.LogWarning(exception, "Metrics catch-up failed");
        }
        finally
        {
            await binder.ReleaseHeldPushesAsync();
        }
    }

    // False only when the module was disposed mid-loop, which is the one way the loop ends without
    // a start behind it. Everything else is retried.
    private async Task<bool> StartUntilItSucceedsAsync()
    {
        var previousRetryCount = 0L;

        while (!_disposed)
        {
            try
            {
                await hub.StartAsync();
                return true;
            }
            catch (Exception exception)
            {
                // Swallowed on purpose: automatic reconnection never covers the first attempt, so
                // this loop is what covers it, and there is no caller who could do anything with
                // the failure.
                logger.LogWarning(exception, "Metrics hub start failed, retrying");
            }

            await Task.Delay(MetricsRetryPolicy.DelayFor(previousRetryCount), timeProvider);
            previousRetryCount++;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        binder.Unbind();
        await hub.DisposeAsync();
    }
}