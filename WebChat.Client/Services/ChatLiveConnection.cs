using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class ChatLiveConnection(
    IHubConnectionFactory connectionFactory,
    IHubEventBinder eventBinder,
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

    public HubConnection? HubConnection => _connection?.Connection;

    public Task ConnectAsync() => StartLiveConnectionAsync(CancellationToken.None);

    private async Task StartLiveConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
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
            _connectionEventDispatcher.HandleReconnected();
            return Task.CompletedTask;
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
            return;
        }

        // Publishing Connected advances the connection epoch, which is what session recovery
        // and catch-up are keyed on. Neither runs on the first one.
        _connectionEventDispatcher.HandleConnected();
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

            var becameLive = await TryBecomeLiveAsync();
            if (_disposed || becameLive)
            {
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

    private async Task<bool> TryBecomeLiveAsync()
    {
        try
        {
            // Bound each attempt: right after an Android resume the radio may not be up
            // yet, and an unbounded StartAsync can hang on a dead handshake for tens of
            // seconds — the exact stall this rebuild exists to escape.
            using var cts = new CancellationTokenSource(_rebuildAttemptTimeout, timeProvider);
            await StartLiveConnectionAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
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