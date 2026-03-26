using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Connection;

namespace Tests.Unit.WebChat.Client.State;

public class ConnectionStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectionStore _store;

    public ConnectionStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new ConnectionStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void ConnectionConnected_SetsLastConnectedAndClearsErrorAndReconnectAttempts()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionError("Previous error"));

        var beforeConnect = DateTime.UtcNow;
        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.LastConnected.ShouldNotBeNull();
        _store.State.LastConnected.Value.ShouldBeGreaterThanOrEqualTo(beforeConnect);
        _store.State.Error.ShouldBeNull();
        _store.State.ReconnectAttempts.ShouldBe(0);
    }

    [Fact]
    public void ConnectionReconnecting_IncrementsReconnectAttempts()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _store.State.ReconnectAttempts.ShouldBe(1);

        _dispatcher.Dispatch(new ConnectionReconnecting());
        _store.State.ReconnectAttempts.ShouldBe(2);

        _dispatcher.Dispatch(new ConnectionReconnecting());
        _store.State.ReconnectAttempts.ShouldBe(3);
    }

    [Fact]
    public void ConnectionClosed_SetsDisconnectedAndOptionalError()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionClosed("Server shutdown"));

        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.Error.ShouldBe("Server shutdown");
    }

    [Fact]
    public void ConnectionClosed_NullError_ClearsPreviousError()
    {
        _dispatcher.Dispatch(new ConnectionError("Previous error"));
        _dispatcher.Dispatch(new ConnectionClosed(null));

        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void ConnectionError_SetsErrorWithoutChangingStatus()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        var statusBefore = _store.State.Status;

        _dispatcher.Dispatch(new ConnectionError("Timeout"));

        _store.State.Error.ShouldBe("Timeout");
        _store.State.Status.ShouldBe(statusBefore);
    }

    [Fact]
    public void FullConnectionLifecycle_HandledCorrectly()
    {
        // Initial state
        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);

        // Connecting
        _dispatcher.Dispatch(new ConnectionConnecting());
        _store.State.Status.ShouldBe(ConnectionStatus.Connecting);

        // Connected
        _dispatcher.Dispatch(new ConnectionConnected());
        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.LastConnected.ShouldNotBeNull();

        // Connection lost, reconnecting
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _store.State.Status.ShouldBe(ConnectionStatus.Reconnecting);
        _store.State.ReconnectAttempts.ShouldBe(1);

        // Reconnect attempt
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _store.State.ReconnectAttempts.ShouldBe(2);

        // Reconnected
        _dispatcher.Dispatch(new ConnectionReconnected());
        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.ReconnectAttempts.ShouldBe(0);

        // Graceful close
        _dispatcher.Dispatch(new ConnectionClosed(null));
        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
    }
}
