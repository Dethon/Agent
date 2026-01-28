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
            .WithServerTimeout(TimeSpan.FromMinutes(3))
            .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
            .Build();

        HubConnection.Closed += exception =>
        {
            _connectionEventDispatcher.HandleClosed(exception);
            OnStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        HubConnection.Reconnecting += _ =>
        {
            _connectionEventDispatcher.HandleReconnecting();
            OnReconnecting?.Invoke();
            OnStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        HubConnection.Reconnected += async _ =>
        {
            _connectionEventDispatcher.HandleReconnected();
            if (OnReconnected is not null)
            {
                await OnReconnected.Invoke();
            }

            OnStateChanged?.Invoke();
        };

        _connectionEventDispatcher.HandleConnecting();
        await HubConnection.StartAsync();
        _connectionEventDispatcher.HandleConnected();
        OnStateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
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
        return TimeSpan.FromSeconds(1);
    }
}