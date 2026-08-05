namespace Dashboard.Client.Contracts;

// One transport instance underneath the dashboard's live connection. The receive verb is generic
// over the wire method name, so a twelfth server push is a line in the binder rather than a member
// here, a member on the implementation and a member on the fake. Whether the transport is up is not
// among the members: liveness is published by the live connection, out of the lifecycle events, and
// a second answer on this seam would be one nothing reads and everything could disagree with.
public interface IMetricsHubConnection : IAsyncDisposable
{
    event Func<Exception?, Task>? Closed;
    event Func<Exception?, Task>? Reconnecting;
    event Func<string?, Task>? Reconnected;
    IDisposable On<T>(string methodName, Func<T, Task> handler);
    Task StartAsync(CancellationToken cancellationToken = default);
}