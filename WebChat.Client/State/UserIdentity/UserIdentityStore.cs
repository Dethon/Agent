namespace WebChat.Client.State.UserIdentity;

public sealed class UserIdentityStore : IDisposable
{
    private readonly Store<UserIdentityState> _store;

    public UserIdentityStore(Dispatcher dispatcher)
    {
        _store = new Store<UserIdentityState>(UserIdentityState.Initial);

        dispatcher.RegisterHandler<LoadUsers>(action =>
            _store.Dispatch(action, UserIdentityReducers.Reduce));

        dispatcher.RegisterHandler<UsersLoaded>(action =>
            _store.Dispatch(action, UserIdentityReducers.Reduce));

        dispatcher.RegisterHandler<SelectUser>(action =>
            _store.Dispatch(action, UserIdentityReducers.Reduce));

        dispatcher.RegisterHandler<ClearUser>(action =>
            _store.Dispatch(action, UserIdentityReducers.Reduce));
    }

    public UserIdentityState State => _store.State;

    public IObservable<UserIdentityState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();
}
