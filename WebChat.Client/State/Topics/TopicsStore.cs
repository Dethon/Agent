namespace WebChat.Client.State.Topics;

public sealed class TopicsStore : IDisposable
{
    private readonly Store<TopicsState> _store;

    public TopicsStore(Dispatcher dispatcher)
    {
        _store = new Store<TopicsState>(TopicsState.Initial);

        // Register handlers for all topic actions
        dispatcher.RegisterHandler<LoadTopics>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<TopicsLoaded>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<SelectTopic>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<AddTopic>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<UpdateTopic>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<RemoveTopic>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<SetAgents>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<SelectAgent>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<TopicsError>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));

        dispatcher.RegisterHandler<CreateNewTopic>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));
    }

    public TopicsState State => _store.State;

    public IObservable<TopicsState> StateObservable => _store.StateObservable;

    public void Dispose()
    {
        _store.Dispose();
    }
}