using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class ChatConnectionService(
    HttpClient httpClient,
    ConnectionEventDispatcher connectionEventDispatcher) : IChatConnectionService
{
    private readonly ConnectionEventDispatcher _connectionEventDispatcher = connectionEventDispatcher;
    public bool IsConnected => HubConnection?.State == HubConnectionState.Connected;
    public bool IsReconnecting => HubConnection?.State == HubConnectionState.Reconnecting;

    internal HubConnection? HubConnection { get; private set; }

    public event Action? OnStateChanged;
    public event Func<Task>? OnReconnected;
    public event Action? OnReconnecting;

    public async Task ConnectAsync()
    {
        if (HubConnection is not null)
        {
            return;
        }

        var config = await httpClient.GetFromJsonAsync<AppConfig>("/api/config");
        var agentUrl = config?.AgentUrl ?? "http://localhost:5000";
        var hubUrl = $"{agentUrl.TrimEnd('/')}/hubs/chat";

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

internal record AppConfig(string? AgentUrl);

internal sealed class AggressiveRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] _retryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    private static readonly TimeSpan _maxDelay = TimeSpan.FromSeconds(10);

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var attempt = retryContext.PreviousRetryCount;
        return attempt < _retryDelays.Length ? _retryDelays[attempt] : _maxDelay;
    }
}