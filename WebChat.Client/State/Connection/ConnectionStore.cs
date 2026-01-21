namespace WebChat.Client.State.Connection;

public sealed class ConnectionStore : IDisposable
{
    private readonly Store<ConnectionState> _store;

    public ConnectionStore(Dispatcher dispatcher)
    {
        _store = new Store<ConnectionState>(ConnectionState.Initial);

        dispatcher.RegisterHandler<ConnectionStatusChanged>(action =>
            _store.Dispatch(action, ConnectionReducers.Reduce));
        dispatcher.RegisterHandler<ConnectionConnecting>(action =>
            _store.Dispatch(action, ConnectionReducers.Reduce));
        dispatcher.RegisterHandler<ConnectionConnected>(action =>
            _store.Dispatch(action, ConnectionReducers.Reduce));
        dispatcher.RegisterHandler<ConnectionReconnecting>(action =>
            _store.Dispatch(action, ConnectionReducers.Reduce));
        dispatcher.RegisterHandler<ConnectionReconnected>(action =>
            _store.Dispatch(action, ConnectionReducers.Reduce));
        dispatcher.RegisterHandler<ConnectionClosed>(action =>
            _store.Dispatch(action, ConnectionReducers.Reduce));
        dispatcher.RegisterHandler<ConnectionError>(action =>
            _store.Dispatch(action, ConnectionReducers.Reduce));
    }

    public ConnectionState State => _store.State;

    public IObservable<ConnectionState> StateObservable => _store.StateObservable;

    public void Dispose()
    {
        _store.Dispose();
    }
}