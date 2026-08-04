using Dashboard.Client.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Tests.Unit.Dashboard.Client.Fixtures;

// One handler registry keyed by wire method name, so a new server push costs a call to RaiseAsync
// and nothing else. It can also fail a scripted number of starts and raise the three lifecycle
// events, which is what makes the behaviour around an interruption expressible in a test at all.
public sealed class FakeMetricsHubConnection : IMetricsHubConnection
{
    private readonly Dictionary<string, List<Delegate>> _handlers = [];

    public HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;

    public int StartAttempts { get; private set; }

    public int FailedStartsRemaining { get; set; }

    public bool Disposed { get; private set; }

    public IReadOnlyCollection<string> BoundWireNames => _handlers
        .Where(pair => pair.Value.Count > 0)
        .Select(pair => pair.Key)
        .ToList();

    public event Func<Exception?, Task>? Closed;
    public event Func<Exception?, Task>? Reconnecting;
    public event Func<string?, Task>? Reconnected;

    public IDisposable On<T>(string methodName, Func<T, Task> handler)
    {
        var registered = _handlers.TryGetValue(methodName, out var existing)
            ? existing
            : _handlers[methodName] = [];

        registered.Add(handler);
        return new Registration(registered, handler);
    }

    public Task RaiseAsync<T>(string methodName, T payload) =>
        _handlers.TryGetValue(methodName, out var registered)
            ? Task.WhenAll(registered.OfType<Func<T, Task>>().ToList().Select(handler => handler(payload)))
            : Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartAttempts++;
        State = HubConnectionState.Connecting;
        await Task.Yield();

        if (FailedStartsRemaining > 0)
        {
            FailedStartsRemaining--;
            State = HubConnectionState.Disconnected;
            throw new InvalidOperationException("hub unavailable");
        }

        State = HubConnectionState.Connected;
    }

    public Task RaiseClosedAsync(Exception? exception)
    {
        State = HubConnectionState.Disconnected;
        return Closed?.Invoke(exception) ?? Task.CompletedTask;
    }

    public Task RaiseReconnectingAsync(Exception? exception)
    {
        State = HubConnectionState.Reconnecting;
        return Reconnecting?.Invoke(exception) ?? Task.CompletedTask;
    }

    public Task RaiseReconnectedAsync(string? connectionId = "connection-1")
    {
        State = HubConnectionState.Connected;
        return Reconnected?.Invoke(connectionId) ?? Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        State = HubConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private sealed class Registration(List<Delegate> registered, Delegate handler) : IDisposable
    {
        public void Dispose() => registered.Remove(handler);
    }
}