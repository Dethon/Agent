namespace WebChat.Client.State.Space;

public sealed class SpaceStore : IDisposable
{
    private readonly Store<SpaceState> _store;

    // Handlers registered here run synchronously before SpaceEffect's async handler,
    // so effects can read up-to-date state (e.g. CurrentSlug) immediately after dispatch.
    public SpaceStore(Dispatcher dispatcher)
    {
        _store = new Store<SpaceState>(SpaceState.Initial);

        dispatcher.RegisterHandler<SelectSpace>(action =>
            _store.Dispatch(action, SpaceReducers.Reduce));
        dispatcher.RegisterHandler<SpaceValidated>(action =>
            _store.Dispatch(action, SpaceReducers.Reduce));
        dispatcher.RegisterHandler<InvalidSpace>(action =>
            _store.Dispatch(action, SpaceReducers.Reduce));
    }

    public SpaceState State => _store.State;
    public IObservable<SpaceState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();
}
