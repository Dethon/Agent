using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed record HubCall(string MethodName, object?[] Arguments);

public sealed class FakeHubConnection : IChatHubConnection
{
    private readonly Dictionary<string, List<Delegate>> _handlers = [];
    private readonly Dictionary<string, Func<object?[], object?>> _answers = [];
    private readonly List<HubCall> _calls = [];

    public HubConnection? Connection => null;

    public IReadOnlyList<HubCall> Calls => _calls;

    public void Answer(string methodName, object? answer) => _answers[methodName] = _ => answer;

    public void Answer(string methodName, Func<object?[], object?> answer) => _answers[methodName] = answer;
    public HubConnectionState State { get; set; } = HubConnectionState.Disconnected;
    public Func<CancellationToken, Task> StartBehavior { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task<bool>> PingBehavior { get; set; } = _ => Task.FromResult(true);
    public bool Disposed { get; private set; }

    public IReadOnlyCollection<string> BoundWireNames => _handlers
        .Where(pair => pair.Value.Count > 0)
        .Select(pair => pair.Key)
        .ToList();

    public event Func<Exception?, Task>? Closed;
    public event Func<Exception?, Task>? Reconnecting;
    public event Func<string?, Task>? Reconnected;

    public IDisposable On<T>(string methodName, Action<T> handler)
    {
        var registered = _handlers.TryGetValue(methodName, out var existing)
            ? existing
            : _handlers[methodName] = [];

        registered.Add(handler);
        return new Registration(registered, handler);
    }

    public void Raise<T>(string methodName, T payload)
    {
        if (!_handlers.TryGetValue(methodName, out var registered))
        {
            return;
        }

        registered.OfType<Action<T>>().ToList().ForEach(handler => handler(payload));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        State = HubConnectionState.Connecting;
        try
        {
            await StartBehavior(cancellationToken);
        }
        catch
        {
            State = HubConnectionState.Disconnected;
            throw;
        }

        State = HubConnectionState.Connected;
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken) => PingBehavior(cancellationToken);

    public Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args)
    {
        _calls.Add(new HubCall(methodName, args));
        var answer = _answers.TryGetValue(methodName, out var scripted) ? (T?)scripted(args) : default;
        return Task.FromResult(HubResult<T>.Answered(answer));
    }

    public Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args)
    {
        _calls.Add(new HubCall(methodName, args));
        _answers.TryGetValue(methodName, out var scripted);
        scripted?.Invoke(args);
        return Task.FromResult(HubResult<Nothing>.Answered(default));
    }

    public Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args)
    {
        _calls.Add(new HubCall(methodName, args));
        var stream = _answers.TryGetValue(methodName, out var scripted)
            ? (IAsyncEnumerable<T>)scripted(args)!
            : EmptyStream<T>();
        return Task.FromResult(HubResult<IAsyncEnumerable<T>>.Answered(stream));
    }

    private static async IAsyncEnumerable<T> EmptyStream<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        State = HubConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    public Task RaiseClosedAsync(Exception? exception) => Closed?.Invoke(exception) ?? Task.CompletedTask;

    public Task RaiseReconnectingAsync(Exception? exception) => Reconnecting?.Invoke(exception) ?? Task.CompletedTask;

    public Task RaiseReconnectedAsync(string? connectionId) => Reconnected?.Invoke(connectionId) ?? Task.CompletedTask;

    private sealed class Registration(List<Delegate> registered, Delegate handler) : IDisposable
    {
        public void Dispose() => registered.Remove(handler);
    }
}