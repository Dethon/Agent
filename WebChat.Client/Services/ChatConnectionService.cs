using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class ChatConnectionService(
    ConfigService configService,
    ConnectionEventDispatcher connectionEventDispatcher,
    NavigationManager navigationManager) : IChatConnectionService
{
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(1.5);

    private readonly ConnectionEventDispatcher _connectionEventDispatcher = connectionEventDispatcher;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private bool _disposed;
    private bool _hasConnectedBefore;

    public bool IsConnected => HubConnection?.State == HubConnectionState.Connected;
    public bool IsReconnecting => HubConnection?.State == HubConnectionState.Reconnecting;

    public HubConnection? HubConnection { get; private set; }

    public event Action? OnStateChanged;
    public event Func<Task>? OnReconnected;
    public event Action? OnReconnecting;

    public async Task ConnectAsync()
    {
        if (HubConnection is not null)
        {
            return;
        }

        var config = await configService.GetConfigAsync();
        var isHttps = navigationManager.BaseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // When on HTTPS (through reverse proxy), use same origin to go through the proxy
        // This avoids mixed content issues and allows the proxy to route SignalR properly
        var hubUrl = string.IsNullOrEmpty(config.AgentUrl) || isHttps
            ? navigationManager.ToAbsoluteUri("/hubs/chat").ToString()
            : $"{config.AgentUrl.TrimEnd('/')}/hubs/chat";

        HubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new AggressiveRetryPolicy())
            .WithServerTimeout(TimeSpan.FromMinutes(6))
            .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
            .Build();

        HubConnection.Closed += OnConnectionClosed;

        HubConnection.Reconnecting += _ =>
        {
            _connectionEventDispatcher.HandleReconnecting();
            OnReconnecting?.Invoke();
            OnStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        HubConnection.Reconnected += connectionId =>
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
        await HubConnection.StartAsync();

        // The service may have been disposed while StartAsync was in flight (e.g. the circuit
        // tore down mid-rebuild). Don't publish state or fire recovery into a dead store —
        // drop the just-started connection instead of leaking it.
        if (_disposed)
        {
            await HubConnection.DisposeAsync();
            HubConnection = null;
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

            var action = ForegroundReconnectPolicy.Decide(HubConnection?.State);
            if (action == ForegroundAction.NoOp)
            {
                return;
            }

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
        var connection = HubConnection;
        if (connection is null)
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(_probeTimeout);
            return await connection.InvokeAsync<bool>("Ping", cts.Token);
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
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await ReconnectIfNeededAsync();
    }

    private async Task RebuildAsync()
    {
        if (HubConnection is not null)
        {
            // Detach first: the connection we're tearing down dispatches its Closed callback
            // fire-and-forget off the receive loop, so leaving it attached lets that stale
            // callback later race the fresh connection (flip the UI to Disconnected over a live
            // socket, or fire a redundant reconnect).
            HubConnection.Closed -= OnConnectionClosed;
            await HubConnection.DisposeAsync();
            HubConnection = null;

            // Drive the Disconnected transition deterministically and in order on this task
            // before reconnecting. ReconnectionEffect only arms its reload on a
            // Disconnected/Reconnecting status, so without this the topic/history/stream reload
            // could be skipped when the new connection's Connected dispatch wins the race.
            _connectionEventDispatcher.HandleClosed(null);
        }

        try
        {
            await ConnectAsync();
        }
        catch
        {
            // Still unreachable (e.g. offline). ConnectAsync leaves a non-null, never-started
            // connection that won't auto-reconnect, so reset to a clean Disconnected state and
            // let the online/visibility listeners retry on the next resume rather than getting
            // stuck — and don't let the failure escape uncaught into OnPageVisible.
            if (HubConnection is not null)
            {
                HubConnection.Closed -= OnConnectionClosed;
                await HubConnection.DisposeAsync();
                HubConnection = null;
            }

            _connectionEventDispatcher.HandleClosed(null);
            OnStateChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (HubConnection is not null)
        {
            await HubConnection.DisposeAsync();
        }
    }
}

internal sealed class AggressiveRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // First retry is immediate for fast mobile resume; subsequent retries
        // back off slightly to avoid hammering a temporarily unavailable server.
        return retryContext.PreviousRetryCount == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1);
    }
}