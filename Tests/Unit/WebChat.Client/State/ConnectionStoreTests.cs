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
    public void Initial_IsDisconnectedWithNothingRecorded()
    {
        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.LastConnected.ShouldBeNull();
        _store.State.ReconnectAttempts.ShouldBe(0);
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void Connecting_SetsStatusConnecting()
    {
        _dispatcher.Dispatch(new ConnectionConnecting());

        _store.State.Status.ShouldBe(ConnectionStatus.Connecting);
    }

    [Fact]
    public void Connecting_LeavesAPreviousErrorInPlace()
    {
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        _dispatcher.Dispatch(new ConnectionConnecting());

        _store.State.Error.ShouldBe("hub dropped");
    }

    [Fact]
    public void Connected_SetsStatusRecordsTheTimeAndClearsTheError()
    {
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        var before = DateTime.UtcNow;

        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.Error.ShouldBeNull();
        _store.State.LastConnected.ShouldNotBeNull();
        _store.State.LastConnected.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void Connected_ResetsTheReconnectAttempts()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.ReconnectAttempts.ShouldBe(0);
    }

    [Fact]
    public void Reconnecting_SetsStatusAndCountsTheAttempt()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _store.State.Status.ShouldBe(ConnectionStatus.Reconnecting);
        _store.State.ReconnectAttempts.ShouldBe(1);
    }

    [Fact]
    public void Reconnecting_CountsEveryAttempt()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _store.State.ReconnectAttempts.ShouldBe(3);
    }

    [Fact]
    public void Reconnecting_LeavesAPreviousErrorInPlace()
    {
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _store.State.Error.ShouldBe("hub dropped");
    }

    [Fact]
    public void Reconnected_SetsStatusRecordsTheTimeAndClearsTheError()
    {
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        _dispatcher.Dispatch(new ConnectionReconnecting());
        var before = DateTime.UtcNow;

        _dispatcher.Dispatch(new ConnectionReconnected());

        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.Error.ShouldBeNull();
        _store.State.ReconnectAttempts.ShouldBe(0);
        _store.State.LastConnected.ShouldNotBeNull();
        _store.State.LastConnected.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void Closed_SetsStatusDisconnectedAndKeepsTheError()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));

        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.Error.ShouldBe("hub dropped");
    }

    [Fact]
    public void Closed_WithoutAnError_LeavesTheErrorNull()
    {
        _dispatcher.Dispatch(new ConnectionClosed(null));

        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void Closed_KeepsTheLastConnectedTimeAndTheReconnectAttempts()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        var connectedAt = _store.State.LastConnected;
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));

        _store.State.LastConnected.ShouldBe(connectedAt);
        _store.State.ReconnectAttempts.ShouldBe(1);
    }

    [Fact]
    public void FullLifecycle_ConnectDropReconnectRecover()
    {
        _dispatcher.Dispatch(new ConnectionConnecting());
        _store.State.Status.ShouldBe(ConnectionStatus.Connecting);

        _dispatcher.Dispatch(new ConnectionConnected());
        _store.State.Status.ShouldBe(ConnectionStatus.Connected);

        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);

        _dispatcher.Dispatch(new ConnectionReconnecting());
        _store.State.Status.ShouldBe(ConnectionStatus.Reconnecting);
        _store.State.ReconnectAttempts.ShouldBe(1);

        _dispatcher.Dispatch(new ConnectionReconnected());
        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.ReconnectAttempts.ShouldBe(0);
        _store.State.Error.ShouldBeNull();
    }
}