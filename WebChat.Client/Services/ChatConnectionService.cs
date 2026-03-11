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
    private readonly ConnectionEventDispatcher _connectionEventDispatcher = connectionEventDispatcher;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private bool _disposed;

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

        HubConnection.Closed += async exception =>
        {
            _connectionEventDispatcher.HandleClosed(exception);
            OnStateChanged?.Invoke();

            // On mobile, the browser suspends JS when backgrounded, so SignalR's
            // automatic reconnect can't run. When the app resumes, the transport
            // may be dead and all queued retries fail at once, firing Closed.
            // Wait briefly then rebuild the connection from scratch.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            await ReconnectIfNeededAsync();
        };

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
        _connectionEventDispatcher.HandleConnected();
        OnStateChanged?.Invoke();
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
            if (_disposed || HubConnection is null)
            {
                return;
            }

            if (HubConnection.State is HubConnectionState.Connected
                or HubConnectionState.Reconnecting
                or HubConnectionState.Connecting)
            {
                return;
            }

            // Connection is disconnected — dispose and rebuild
            await HubConnection.DisposeAsync();
            HubConnection = null;
            await ConnectAsync();
        }
        finally
        {
            _reconnectLock.Release();
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