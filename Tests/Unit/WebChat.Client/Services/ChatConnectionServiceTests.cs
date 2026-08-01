using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WebChat.Client.Contracts;
using WebChat.Client.Services;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Hub;

namespace Tests.Unit.WebChat.Client.Services;

public class ChatConnectionServiceTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FakeHubConnectionFactory _factory = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly ChatConnectionService _service;

    public ChatConnectionServiceTests()
    {
        _service = new ChatConnectionService(_factory, new ConnectionEventDispatcher(_dispatcher), _timeProvider);
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_StuckReconnectingConnection_ReplacesItWithFreshConnection()
    {
        await _service.ConnectAsync();
        var stuck = _factory.Created.Single();
        stuck.State = HubConnectionState.Reconnecting;

        await _service.ReconnectIfNeededAsync();

        stuck.Disposed.ShouldBeTrue();
        _factory.Created.Count.ShouldBe(2);
        _service.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_AfterRebuild_FiresOnReconnectedRecovery()
    {
        await _service.ConnectAsync();
        var recovered = false;
        _service.OnReconnected += () =>
        {
            recovered = true;
            return Task.CompletedTask;
        };
        _factory.Created.Single().State = HubConnectionState.Reconnecting;

        await _service.ReconnectIfNeededAsync();

        recovered.ShouldBeTrue();
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_HungConnectAttempt_IsCancelledAtAttemptTimeoutAndRetried()
    {
        await _service.ConnectAsync();
        _factory.Created.Single().State = HubConnectionState.Reconnecting;
        var hung = new FakeHubConnection { StartBehavior = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        _factory.Enqueue(hung);

        var reconnect = _service.ReconnectIfNeededAsync();

        // Just short of the 2.5s per-attempt timeout the hung attempt must still be running.
        _timeProvider.Advance(TimeSpan.FromSeconds(2.4));
        await SettleAsync();
        reconnect.IsCompleted.ShouldBeFalse();
        _factory.Created.Count.ShouldBe(2);

        await AdvanceUntilCompleteAsync(reconnect);

        hung.Disposed.ShouldBeTrue();
        _factory.Created.Count.ShouldBe(3);
        _service.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_ServerUnreachable_GivesUpDisconnectedAfterFourAttempts()
    {
        await _service.ConnectAsync();
        _factory.Created.Single().State = HubConnectionState.Reconnecting;
        _factory.CreateBehavior = () => new FakeHubConnection
        {
            StartBehavior = _ => Task.FromException(new IOException("connection refused"))
        };

        var reconnect = _service.ReconnectIfNeededAsync();
        await AdvanceUntilCompleteAsync(reconnect);

        _factory.Created.Count.ShouldBe(5); // the original connection + 4 rebuild attempts
        _service.IsConnected.ShouldBeFalse();
        _dispatcher.Actions.Last().ShouldBeOfType<ConnectionClosed>();
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_ConnectedAndPingSucceeds_KeepsConnection()
    {
        await _service.ConnectAsync();
        var live = _factory.Created.Single();

        await _service.ReconnectIfNeededAsync();

        live.Disposed.ShouldBeFalse();
        _factory.Created.Count.ShouldBe(1);
        _service.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_ConnectedButPingFails_Rebuilds()
    {
        await _service.ConnectAsync();
        var zombie = _factory.Created.Single();
        zombie.PingBehavior = _ => Task.FromException<bool>(new IOException("transport dead"));

        await _service.ReconnectIfNeededAsync();

        zombie.Disposed.ShouldBeTrue();
        _factory.Created.Count.ShouldBe(2);
        _service.IsConnected.ShouldBeTrue();
    }

    private async Task AdvanceUntilCompleteAsync(Task task)
    {
        foreach (var _ in Enumerable.Range(0, 20).TakeWhile(_ => !task.IsCompleted))
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
            await SettleAsync();
        }

        task.IsCompleted.ShouldBeTrue();
        await task;
    }

    // Real (not fake) delay so continuations queued on the thread pool by a time
    // advance get a chance to run before the next assertion or advance.
    private static Task SettleAsync() => Task.Delay(50);

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Actions { get; } = [];

        public void Dispatch<TAction>(TAction action) where TAction : IAction
        {
            Actions.Add(action);
        }
    }

    private sealed class FakeHubConnectionFactory : IHubConnectionFactory
    {
        private readonly Queue<FakeHubConnection> _scripted = new();

        public List<FakeHubConnection> Created { get; } = [];
        public Func<FakeHubConnection> CreateBehavior { get; set; } = () => new FakeHubConnection();

        public void Enqueue(FakeHubConnection connection) => _scripted.Enqueue(connection);

        public Task<IChatHubConnection> CreateAsync()
        {
            var connection = _scripted.TryDequeue(out var scripted) ? scripted : CreateBehavior();
            Created.Add(connection);
            return Task.FromResult<IChatHubConnection>(connection);
        }
    }

    private sealed class FakeHubConnection : IChatHubConnection
    {
        public HubConnection? Connection => null;
        public HubConnectionState State { get; set; } = HubConnectionState.Disconnected;
        public Func<CancellationToken, Task> StartBehavior { get; set; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task<bool>> PingBehavior { get; set; } = _ => Task.FromResult(true);
        public bool Disposed { get; private set; }

        public event Func<Exception?, Task>? Closed;
        public event Func<Exception?, Task>? Reconnecting;
        public event Func<string?, Task>? Reconnected;

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

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            State = HubConnectionState.Disconnected;
            return ValueTask.CompletedTask;
        }

        public Task RaiseClosedAsync(Exception? exception) => Closed?.Invoke(exception) ?? Task.CompletedTask;

        public Task RaiseReconnectingAsync(Exception? exception) => Reconnecting?.Invoke(exception) ?? Task.CompletedTask;

        public Task RaiseReconnectedAsync(string? connectionId) => Reconnected?.Invoke(connectionId) ?? Task.CompletedTask;
    }
}