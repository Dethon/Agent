using System.Reactive.Linq;

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

    // Every epoch after the one a subscriber first sees — that is, each interruption it
    // lived through. Both catch-up and session recovery are keyed on this, so the rule that
    // neither runs on the first connection is written here once instead of in each of them.
    public IObservable<int> BecameLiveAgain => _store.StateObservable
        .Where(state => state.Status == ConnectionStatus.Connected)
        .Select(state => state.Epoch)
        .DistinctUntilChanged()
        .Skip(1);

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