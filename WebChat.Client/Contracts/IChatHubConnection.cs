using Microsoft.AspNetCore.SignalR.Client;

namespace WebChat.Client.Contracts;

public interface IChatHubConnection : IAsyncDisposable
{
    HubConnection? Connection { get; }
    HubConnectionState State { get; }
    event Func<Exception?, Task>? Closed;
    event Func<Exception?, Task>? Reconnecting;
    event Func<string?, Task>? Reconnected;
    IDisposable On<T>(string methodName, Action<T> handler);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<bool> PingAsync(CancellationToken cancellationToken);
}