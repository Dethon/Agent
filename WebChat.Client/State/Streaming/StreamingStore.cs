namespace WebChat.Client.State.Streaming;

public sealed class StreamingStore : IDisposable
{
    private readonly Store<StreamingState> _store;

    public StreamingStore(Dispatcher dispatcher)
    {
        _store = new Store<StreamingState>(StreamingState.Initial);

        dispatcher.RegisterHandler<StreamStarted>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<StreamChunk>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<StreamCompleted>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<StreamCancelled>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<ResetStreamingContent>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<StreamError>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<StartResuming>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<StopResuming>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<RequestContentFinalization>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
        dispatcher.RegisterHandler<ClearFinalizationRequest>(action =>
            _store.Dispatch(action, StreamingReducers.Reduce));
    }

    public StreamingState State => _store.State;

    public IObservable<StreamingState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();
}