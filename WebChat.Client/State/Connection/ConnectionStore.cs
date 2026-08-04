namespace WebChat.Client.State.Connection;

public record ConnectionConnecting : IAction;

public record ConnectionConnected : IAction;

public record ConnectionReconnecting : IAction;

public record ConnectionReconnected : IAction;

public record ConnectionClosed(string? Error) : IAction;

public sealed class ConnectionStore : IDisposable
{
    private readonly Store<ConnectionState> _store;

    public ConnectionStore(Dispatcher dispatcher)
    {
        _store = new Store<ConnectionState>(ConnectionState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public ConnectionState State => _store.State;

    public IObservable<ConnectionState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static ConnectionState Reduce(ConnectionState state, IAction action) => action switch
    {
        ConnectionConnecting => state with
        {
            Status = ConnectionStatus.Connecting
        },

        ConnectionConnected => state with
        {
            Status = ConnectionStatus.Connected,
            LastConnected = DateTime.UtcNow,
            ReconnectAttempts = 0,
            Error = null,
            Epoch = state.Epoch + 1
        },

        ConnectionReconnecting => state with
        {
            Status = ConnectionStatus.Reconnecting,
            ReconnectAttempts = state.ReconnectAttempts + 1
        },

        ConnectionReconnected => state with
        {
            Status = ConnectionStatus.Connected,
            LastConnected = DateTime.UtcNow,
            ReconnectAttempts = 0,
            Error = null,
            Epoch = state.Epoch + 1
        },

        ConnectionClosed a => state with
        {
            Status = ConnectionStatus.Disconnected,
            Error = a.Error
        },

        _ => state
    };
}