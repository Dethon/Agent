namespace WebChat.Client.State.ConnectedAccounts;

public sealed class ConnectedAccountsStore : IDisposable
{
    private readonly Store<ConnectedAccountsState> _store;

    public ConnectedAccountsStore(Dispatcher dispatcher)
    {
        _store = new Store<ConnectedAccountsState>(ConnectedAccountsState.Initial);

        dispatcher.RegisterHandler<AccountStatusLoaded>(action =>
            _store.Dispatch(action, ConnectedAccountsReducers.Reduce));
        dispatcher.RegisterHandler<AccountConnected>(action =>
            _store.Dispatch(action, ConnectedAccountsReducers.Reduce));
        dispatcher.RegisterHandler<AccountDisconnected>(action =>
            _store.Dispatch(action, ConnectedAccountsReducers.Reduce));
    }

    public ConnectedAccountsState State => _store.State;

    public IObservable<ConnectedAccountsState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();
}
