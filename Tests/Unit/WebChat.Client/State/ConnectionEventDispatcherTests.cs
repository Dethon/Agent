using Moq;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Hub;

namespace Tests.Unit.WebChat.Client.State;

public sealed class ConnectionEventDispatcherTests
{
    private readonly Mock<IDispatcher> _mockDispatcher;
    private readonly ConnectionEventDispatcher _sut;

    public ConnectionEventDispatcherTests()
    {
        _mockDispatcher = new Mock<IDispatcher>();
        _sut = new ConnectionEventDispatcher(_mockDispatcher.Object);
    }

    [Fact]
    public void HandleConnecting_DispatchesConnectionConnecting()
    {
        _sut.HandleConnecting();

        _mockDispatcher.Verify(d => d.Dispatch(It.IsAny<ConnectionConnecting>()), Times.Once);
    }

    [Fact]
    public void HandleConnected_DispatchesConnectionConnected()
    {
        _sut.HandleConnected();

        _mockDispatcher.Verify(d => d.Dispatch(It.IsAny<ConnectionConnected>()), Times.Once);
    }

    [Fact]
    public void HandleReconnecting_DispatchesConnectionReconnecting()
    {
        _sut.HandleReconnecting(new InvalidOperationException("Connection lost"));

        _mockDispatcher.Verify(d => d.Dispatch(It.IsAny<ConnectionReconnecting>()), Times.Once);
    }

    [Fact]
    public void HandleReconnected_DispatchesConnectionReconnected()
    {
        _sut.HandleReconnected("new-connection-id");

        _mockDispatcher.Verify(d => d.Dispatch(It.IsAny<ConnectionReconnected>()), Times.Once);
    }

    [Fact]
    public void HandleClosed_WithException_DispatchesConnectionClosedWithErrorMessage()
    {
        var exception = new InvalidOperationException("Server shutdown");

        _sut.HandleClosed(exception);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<ConnectionClosed>(action => action.Error == "Server shutdown")),
            Times.Once);
    }

    [Fact]
    public void HandleClosed_WithoutException_DispatchesConnectionClosedWithNull()
    {
        _sut.HandleClosed(null);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<ConnectionClosed>(action => action.Error == null)),
            Times.Once);
    }
}