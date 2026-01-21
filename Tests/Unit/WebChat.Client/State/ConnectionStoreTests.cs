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

    public void Dispose()
    {
        _store.Dispose();
    }

    [Fact]
    public void InitialState_IsDisconnected()
    {
        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.LastConnected.ShouldBeNull();
        _store.State.ReconnectAttempts.ShouldBe(0);
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void ConnectionConnecting_SetsStatusToConnecting()
    {
        _dispatcher.Dispatch(new ConnectionConnecting());

        _store.State.Status.ShouldBe(ConnectionStatus.Connecting);
    }

    [Fact]
    public void ConnectionConnected_SetsStatusToConnected()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
    }

    [Fact]
    public void ConnectionConnected_SetsLastConnected()
    {
        var beforeConnect = DateTime.UtcNow;

        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.LastConnected.ShouldNotBeNull();
        _store.State.LastConnected.Value.ShouldBeGreaterThanOrEqualTo(beforeConnect);
        _store.State.LastConnected.Value.ShouldBeLessThanOrEqualTo(DateTime.UtcNow);
    }

    [Fact]
    public void ConnectionConnected_ClearsError()
    {
        _dispatcher.Dispatch(new ConnectionError("Previous error"));
        _store.State.Error.ShouldBe("Previous error");

        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void ConnectionConnected_ResetsReconnectAttempts()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _store.State.ReconnectAttempts.ShouldBe(2);

        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.ReconnectAttempts.ShouldBe(0);
    }

    [Fact]
    public void ConnectionReconnecting_SetsStatusToReconnecting()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _store.State.Status.ShouldBe(ConnectionStatus.Reconnecting);
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
    public void ConnectionReconnected_BehavesSameAsConnected()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionError("Connection lost"));

        var beforeReconnect = DateTime.UtcNow;
        _dispatcher.Dispatch(new ConnectionReconnected());

        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.Error.ShouldBeNull();
        _store.State.ReconnectAttempts.ShouldBe(0);
        _store.State.LastConnected.ShouldNotBeNull();
        _store.State.LastConnected.Value.ShouldBeGreaterThanOrEqualTo(beforeReconnect);
    }

    [Fact]
    public void ConnectionClosed_SetsStatusToDisconnected()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionClosed(null));

        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
    }

    [Fact]
    public void ConnectionClosed_SetsErrorIfProvided()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionClosed("Server shutdown"));

        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.Error.ShouldBe("Server shutdown");
    }

    [Fact]
    public void ConnectionClosed_ErrorCanBeNull()
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
    public void ConnectionStatusChanged_UpdatesStatusDirectly()
    {
        _dispatcher.Dispatch(new ConnectionStatusChanged(ConnectionStatus.Reconnecting));

        _store.State.Status.ShouldBe(ConnectionStatus.Reconnecting);
    }

    [Fact]
    public void ErrorAutoClearsOnSuccessfulConnection()
    {
        // Set error
        _dispatcher.Dispatch(new ConnectionError("Network error"));
        _store.State.Error.ShouldBe("Network error");

        // Successful connection clears error
        _dispatcher.Dispatch(new ConnectionConnected());
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void ReconnectAttempts_IncrementsOnEachAttempt()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        _store.State.ReconnectAttempts.ShouldBe(0);

        for (var i = 1; i <= 5; i++)
        {
            _dispatcher.Dispatch(new ConnectionReconnecting());
            _store.State.ReconnectAttempts.ShouldBe(i);
        }
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