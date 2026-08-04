using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class ChatLiveConnection(
    IHubConnectionFactory connectionFactory,
    IHubEventBinder eventBinder,
    // Resolved lazily because session recovery makes its hub calls back through this live
    // connection. Injecting it eagerly is a container cycle, and since the interface is
    // registered through a factory the container cannot see it — it recurses building live
    // connections until the process dies.
    Lazy<ISessionRecovery> sessionRecovery,
    ConnectionEventDispatcher connectionEventDispatcher,
    TimeProvider timeProvider) : IChatLiveConnection
{
    private const int MaxRebuildAttempts = 4;
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan _closedRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _rebuildAttemptTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan _rebuildRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly ConnectionEventDispatcher _connectionEventDispatcher = connectionEventDispatcher;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private IChatHubConnection? _connection;
    private bool _disposed;
    private bool _hasConnectedBefore;

    public HubConnection? HubConnection => _connection?.Connection;

    public async Task ConnectAsync()
    {
        if (await StartLiveConnectionAsync(CancellationToken.None))
        {
            await sessionRecovery.Value.RecoverAsync();
        }
    }

    // Returns whether session recovery is due, so the caller can run it outside whatever
    // timeout bounded the handshake.
    private async Task<bool> StartLiveConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return false;
        }

        var connection = await connectionFactory.CreateAsync();
        _connection = connection;

        // Bind before starting: a push that arrives immediately after the handshake would
        // otherwise land on a connection with no handlers. Binding here is also what makes a
        // rebuilt connection heard at all — the server pushes belong to the hub connection
        // instance, so a rebuild that skipped this step would leave the client connected and deaf.
        eventBinder.Bind(connection);

        connection.Closed += OnConnectionClosed;

        connection.Reconnecting += _ =>
        {
            _connectionEventDispatcher.HandleReconnecting();
            return Task.CompletedTask;
        };

        connection.Reconnected += _ =>
        {
            // The status is published before recovery runs, so the UI shows Connected at once
            // and does not wait on the re-identification behind it.
            _connectionEventDispatcher.HandleReconnected();
            return sessionRecovery.Value.RecoverAsync();
        };

        _connectionEventDispatcher.HandleConnecting();
        await connection.StartAsync(cancellationToken);

        // The live connection may have been disposed while StartAsync was in flight (e.g. the circuit
        // tore down mid-rebuild). Don't publish state or fire recovery into a dead store —
        // drop the just-started connection instead of leaking it.
        if (_disposed)
        {
            eventBinder.Unbind();
            await connection.DisposeAsync();
            _connection = null;
            return false;
        }

        _connectionEventDispatcher.HandleConnected();

        // A rebuild does NOT raise SignalR's Reconnected event, so recovery has to be run
        // from here on every connect after the first. It is skipped on the first connect
        // because first-load start-up validates the space slug and can replace it before
        // joining — recovering here would join an unvalidated space and then join again.
        var recoveryIsDue = _hasConnectedBefore;
        _hasConnectedBefore = true;
        return recoveryIsDue;
    }

    public async Task ReconnectIfNeededAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!await _reconnectLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            var action = ForegroundReconnectPolicy.Decide(_connection?.State);

            // A reported-Connected connection may be a post-background zombie: the transport
            // is dead but no close event fired, so SignalR still thinks it's up. Verify with a
            // quick round-trip before trusting it. A live connection answers in tens of ms; we
            // only spend the full probe timeout on one that is genuinely dead.
            if (action == ForegroundAction.Probe && await IsConnectionLiveAsync())
            {
                return;
            }

            await RebuildAsync();
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task<bool> IsConnectionLiveAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(_probeTimeout, timeProvider);
            return await connection.PingAsync(cts.Token);
        }
        catch
        {
            // Timeout, transport failure, or a server without the Ping method — treat the
            // connection as dead and let the caller rebuild it.
            return false;
        }
    }

    private async Task OnConnectionClosed(Exception? exception)
    {
        _connectionEventDispatcher.HandleClosed(exception);

        // On mobile, the browser suspends JS when backgrounded, so SignalR's automatic
        // reconnect can't run. When the app resumes the transport may be dead and queued
        // retries fail at once, firing Closed. Wait briefly then rebuild from scratch.
        await Task.Delay(_closedRetryDelay, timeProvider);
        await ReconnectIfNeededAsync();
    }

    private async Task RebuildAsync()
    {
        foreach (var attempt in Enumerable.Range(1, MaxRebuildAttempts))
        {
            await TearDownAsync();

            var recoveryIsDue = await TryBecomeLiveAsync();
            if (_disposed)
            {
                return;
            }

            if (recoveryIsDue is { } isDue)
            {
                // Recovery is awaited here, outside the per-attempt timeout: that timeout
                // bounds the handshake, and stretching it over a slow space rejoin would
                // cancel recovery and retry the rebuild on a perfectly healthy connection.
                if (isDue)
                {
                    await sessionRecovery.Value.RecoverAsync();
                }

                return;
            }

            if (attempt < MaxRebuildAttempts)
            {
                await Task.Delay(_rebuildRetryDelay, timeProvider);
            }
        }

        // Still unreachable (e.g. offline). A failed attempt leaves a non-null, never-started
        // connection that won't auto-reconnect, so reset to a clean Disconnected state and
        // let the online/visibility listeners retry on the next resume rather than getting
        // stuck — and don't let the failure escape uncaught into OnPageVisible.
        await TearDownAsync();
        _connectionEventDispatcher.HandleClosed(null);
    }

    // Null means the attempt failed; otherwise, whether session recovery is due.
    private async Task<bool?> TryBecomeLiveAsync()
    {
        try
        {
            // Bound each attempt: right after an Android resume the radio may not be up
            // yet, and an unbounded StartAsync can hang on a dead handshake for tens of
            // seconds — the exact stall this rebuild exists to escape.
            using var cts = new CancellationTokenSource(_rebuildAttemptTimeout, timeProvider);
            return await StartLiveConnectionAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private async Task TearDownAsync()
    {
        if (_connection is null)
        {
            return;
        }

        // Detach first: the connection we're tearing down dispatches its Closed callback
        // fire-and-forget off the receive loop, so leaving it attached lets that stale
        // callback later race the fresh connection (flip the UI to Disconnected over a live
        // socket, or fire a redundant reconnect).
        _connection.Closed -= OnConnectionClosed;
        eventBinder.Unbind();
        await _connection.DisposeAsync();
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            eventBinder.Unbind();
            await _connection.DisposeAsync();
        }
    }
}