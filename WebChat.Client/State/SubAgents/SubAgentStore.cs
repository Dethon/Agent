namespace WebChat.Client.State.SubAgents;

public sealed class SubAgentStore : IDisposable
{
    private readonly Store<SubAgentState> _store;

    public SubAgentStore(Dispatcher dispatcher)
    {
        _store = new Store<SubAgentState>(SubAgentState.Initial);

        dispatcher.RegisterHandler<SubAgentAnnounced>(action =>
            _store.Dispatch(action, SubAgentReducers.Reduce));
        dispatcher.RegisterHandler<SubAgentUpdated>(action =>
            _store.Dispatch(action, SubAgentReducers.Reduce));
        dispatcher.RegisterHandler<SubAgentRemoved>(action =>
            _store.Dispatch(action, SubAgentReducers.Reduce));
    }

    public SubAgentState State => _store.State;

    public IObservable<SubAgentState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();
}
