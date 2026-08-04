using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class SignalRHubConnectionFactory(
    IConfigService configService,
    NavigationManager navigationManager) : IHubConnectionFactory
{
    public async Task<IChatHubConnection> CreateAsync()
    {
        var config = await configService.GetConfigAsync();
        var isHttps = navigationManager.BaseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // When on HTTPS (through reverse proxy), use same origin to go through the proxy
        // This avoids mixed content issues and allows the proxy to route SignalR properly
        var hubUrl = string.IsNullOrEmpty(config.AgentUrl) || isHttps
            ? navigationManager.ToAbsoluteUri("/hubs/chat").ToString()
            : $"{config.AgentUrl.TrimEnd('/')}/hubs/chat";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new AggressiveRetryPolicy())
            .WithServerTimeout(TimeSpan.FromMinutes(6))
            .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
            .Build();

        return new SignalRHubConnection(connection);
    }
}

internal sealed class SignalRHubConnection(HubConnection connection) : IChatHubConnection
{
    public HubConnection? Connection => connection;
    public HubConnectionState State => connection.State;

    public event Func<Exception?, Task>? Closed
    {
        add => connection.Closed += value;
        remove => connection.Closed -= value;
    }

    public event Func<Exception?, Task>? Reconnecting
    {
        add => connection.Reconnecting += value;
        remove => connection.Reconnecting -= value;
    }

    public event Func<string?, Task>? Reconnected
    {
        add => connection.Reconnected += value;
        remove => connection.Reconnected -= value;
    }

    public IDisposable On<T>(string methodName, Action<T> handler) => connection.On(methodName, handler);

    public Task StartAsync(CancellationToken cancellationToken = default) => connection.StartAsync(cancellationToken);

    public Task<bool> PingAsync(CancellationToken cancellationToken) =>
        connection.InvokeAsync<bool>("Ping", cancellationToken);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
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