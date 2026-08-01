using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class ChatConnectionService(
    IHubConnectionFactory connectionFactory,
    ConnectionEventDispatcher connectionEventDispatcher,
    TimeProvider timeProvider) : IChatConnectionService
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

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;
    public bool IsReconnecting => _connection?.State == HubConnectionState.Reconnecting;

    public HubConnection? HubConnection => _connection?.Connection;

    public event Action? OnStateChanged;
    public event Func<Task>? OnReconnected;
    public event Action? OnReconnecting;

    public Task ConnectAsync() => ConnectAsync(CancellationToken.None);

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
        }

        var connection = await connectionFactory.CreateAsync();
        _connection = connection;

        connection.Closed += OnConnectionClosed;

        connection.Reconnecting += _ =>
        {
            _connectionEventDispatcher.HandleReconnecting();
            OnReconnecting?.Invoke();
            OnStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        connection.Reconnected += connectionId =>
        {
            _connectionEventDispatcher.HandleReconnected();
            OnStateChanged?.Invoke();

            // Run post-reconnection work (re-register user, rejoin space, etc.)
            // without blocking the UI update — keeps "Connected" instant.
            if (OnReconnected is not null)
            {
                _ = OnReconnected.Invoke();
            }

            return Task.CompletedTask;
        };

        _connectionEventDispatcher.HandleConnecting();
        await connection.StartAsync(cancellationToken);

        // The service may have been disposed while StartAsync was in flight (e.g. the circuit
        // tore down mid-rebuild). Don't publish state or fire recovery into a dead store —
        // drop the just-started connection instead of leaking it.
        if (_disposed)
        {
            await connection.DisposeAsync();
            _connection = null;
            return;
        }

        _connectionEventDispatcher.HandleConnected();
        OnStateChanged?.Invoke();

        // A fresh rebuild (dispose + new connection) does NOT raise SignalR's Reconnected
        // event, so the post-reconnection recovery (re-register user, rejoin space,
        // re-subscribe push) wired to OnReconnected would never run on that path. Fire it
        // ourselves on every connect after the first. The first connect is followed by the
        // initialization flow, which does that registration inline.
        if (_hasConnectedBefore && OnReconnected is not null)
        {
            _ = OnReconnected.Invoke();
        }

        _hasConnectedBefore = true;
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
        OnStateChanged?.Invoke();

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

            try
            {
                // Bound each attempt: right after an Android resume the radio may not be up
                // yet, and an unbounded StartAsync can hang on a dead handshake for tens of
                // seconds — the exact stall this rebuild exists to escape.
                using var cts = new CancellationTokenSource(_rebuildAttemptTimeout, timeProvider);
                await ConnectAsync(cts.Token);
                return;
            }
            catch
            {
                if (_disposed)
                {
                    return;
                }
            }

            if (attempt < MaxRebuildAttempts)
            {
                await Task.Delay(_rebuildRetryDelay, timeProvider);
            }
        }

        // Still unreachable (e.g. offline). ConnectAsync leaves a non-null, never-started
        // connection that won't auto-reconnect, so reset to a clean Disconnected state and
        // let the online/visibility listeners retry on the next resume rather than getting
        // stuck — and don't let the failure escape uncaught into OnPageVisible.
        await TearDownAsync();
        OnStateChanged?.Invoke();
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
        await _connection.DisposeAsync();
        _connection = null;

        // Drive the Disconnected transition deterministically and in order on this task
        // before reconnecting. ReconnectionEffect only arms its reload on a
        // Disconnected/Reconnecting status, so without this the topic/history/stream reload
        // could be skipped when the new connection's Connected dispatch wins the race.
        _connectionEventDispatcher.HandleClosed(null);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}