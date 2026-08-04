using Microsoft.AspNetCore.SignalR.Client;

namespace WebChat.Client.Contracts;

public interface IChatHubConnection : IAsyncDisposable
{
    HubConnectionState State { get; }
    event Func<Exception?, Task>? Closed;
    event Func<Exception?, Task>? Reconnecting;
    event Func<string?, Task>? Reconnected;
    IDisposable On<T>(string methodName, Action<T> handler);
    Task StartAsync(CancellationToken cancellationToken = default);
    // The probe stays a bare answer: it is what asks whether the connection is live, so it
    // cannot itself be an answer that depends on the connection being live.
    Task<bool> PingAsync(CancellationToken cancellationToken);

    Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args);
    Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args);
    Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args);
}