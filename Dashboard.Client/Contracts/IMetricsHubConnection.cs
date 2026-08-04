using Microsoft.AspNetCore.SignalR.Client;

namespace Dashboard.Client.Contracts;

// One transport instance underneath the dashboard's live connection. The receive verb is generic
// over the wire method name, so a twelfth server push is a line in the binder rather than a member
// here, a member on the implementation and a member on the fake.
public interface IMetricsHubConnection : IAsyncDisposable
{
    HubConnectionState State { get; }
    event Func<Exception?, Task>? Closed;
    event Func<Exception?, Task>? Reconnecting;
    event Func<string?, Task>? Reconnected;
    IDisposable On<T>(string methodName, Func<T, Task> handler);
    Task StartAsync(CancellationToken cancellationToken = default);
}