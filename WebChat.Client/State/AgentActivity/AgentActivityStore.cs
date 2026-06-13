namespace WebChat.Client.State.AgentActivity;

public sealed class AgentActivityStore : IDisposable
{
    private readonly Store<AgentActivityState> _store;

    public AgentActivityStore(Dispatcher dispatcher)
    {
        _store = new Store<AgentActivityState>(AgentActivityState.Initial);

        dispatcher.RegisterHandler<AllAgentsTopicsMapped>(action =>
            _store.Dispatch(action, AgentActivityReducers.Reduce));
        dispatcher.RegisterHandler<MarkAgentUnseenActivity>(action =>
            _store.Dispatch(action, AgentActivityReducers.Reduce));
        dispatcher.RegisterHandler<ClearAgentUnseenActivity>(action =>
            _store.Dispatch(action, AgentActivityReducers.Reduce));
    }

    public AgentActivityState State => _store.State;
    public IObservable<AgentActivityState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();
}