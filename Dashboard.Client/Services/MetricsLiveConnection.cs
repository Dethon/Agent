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
    DataLoadEffect dataLoad,
    TimeProvider timeProvider,
    ILogger<MetricsLiveConnection> logger) : IAsyncDisposable
{
    private Task? _connecting;
    private bool _started;
    private bool _disposed;
    private bool _awaitingFirstLoadOutcome;
    private bool _holdingUntilCaughtUp;
    private Func<Exception?, Task>? _onReconnecting;
    private Func<string?, Task>? _onReconnected;

    public Task ConnectAsync() => _started ? Task.CompletedTask : _connecting ??= BecomeLiveAsync();

    private async Task BecomeLiveAsync()
    {
        // Bind once, before the first start attempt. A failed start leaves the hub connection and
        // its registrations intact, so rebinding per attempt would double every handler, and a
        // reconnect reuses the same hub connection, so nothing needs rebinding there either.
        binder.Bind(hub);

        dataLoad.LoadCompleted += OnLoadCompletedAsync;

        // Closed is not handled: with a retry policy that never gives up, the transport only closes
        // for good when this module disposes it. The other two are kept in fields because disposal
        // has to take them off again: a reconnect landing afterwards would otherwise drive a whole
        // become-live sequence on a module that is gone.
        _onReconnecting = _ =>
        {
            connectionStore.SetReconnecting();

            // The hold starts here, not in the Reconnected handler: the transport resumes
            // dispatching as soon as it is back and only then runs those handlers, so a push
            // landing in that gap would be applied unheld and erased by the catch-up snapshot.
            HoldUntilCaughtUp();
            return Task.CompletedTask;
        };

        _onReconnected = _ => BecomeLiveAndCatchUpAsync();

        hub.Reconnecting += _onReconnecting;
        hub.Reconnected += _onReconnected;

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

        // Not on a first connection whose page load delivered: catching up as well would double
        // every request on first paint. A load that failed left nothing to double — a dashboard
        // opened during an outage would otherwise show a green dot over empty pages until a manual
        // reload — so a recorded failure makes the first epoch catch up after all.
        if (connectionStore.State.Epoch <= 1 && !dataLoad.LastLoadFailed)
        {
            // The load may still be in flight when this decision is taken, so its outcome settles
            // the skipped premise later: OnLoadCompletedAsync catches up if the load fails.
            _awaitingFirstLoadOutcome = true;
            await ReleaseReconnectHoldAsync();
            return;
        }

        await CatchUpHoldingPushesAsync();
    }

    // The first load to settle after the first epoch skipped its catch-up answers whether the skip
    // was right: a delivery confirms it, a failure asks for the catch-up it was owed. Later loads
    // are ordinary failed requests and settle nothing.
    private Task OnLoadCompletedAsync()
    {
        if (!_awaitingFirstLoadOutcome)
        {
            return Task.CompletedTask;
        }

        _awaitingFirstLoadOutcome = false;
        return dataLoad.LastLoadFailed ? CatchUpHoldingPushesAsync() : Task.CompletedTask;
    }

    // Pushes are held while catch-up replaces the event lists, then released against what the
    // reload delivered: without the hold, an older snapshot erases a push that arrived first,
    // and a push the snapshot already contains lands twice.
    private async Task CatchUpHoldingPushesAsync()
    {
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
            await ReleaseReconnectHoldAsync();
            await binder.ReleaseHeldPushesAsync();
        }
    }

    // The hold an interruption started, ended by the catch-up that answers it. Holds nest, so this
    // release only lowers the depth; the release beside it is the one that delivers.
    private void HoldUntilCaughtUp()
    {
        if (_holdingUntilCaughtUp)
        {
            return;
        }

        _holdingUntilCaughtUp = true;
        binder.HoldPushes();
    }

    private Task ReleaseReconnectHoldAsync()
    {
        if (!_holdingUntilCaughtUp)
        {
            return Task.CompletedTask;
        }

        _holdingUntilCaughtUp = false;
        return binder.ReleaseHeldPushesAsync();
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
        dataLoad.LoadCompleted -= OnLoadCompletedAsync;
        hub.Reconnecting -= _onReconnecting;
        hub.Reconnected -= _onReconnected;
        binder.Unbind();
        await hub.DisposeAsync();
    }
}