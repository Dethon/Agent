namespace WebChat.Client.State.Messages;

public sealed class MessagesStore : IDisposable
{
    private readonly Store<MessagesState> _store;

    public MessagesStore(Dispatcher dispatcher)
    {
        _store = new Store<MessagesState>(MessagesState.Initial);

        // Register handlers for all message actions
        dispatcher.RegisterHandler<MessagesLoaded>(action =>
            _store.Dispatch(action, MessagesReducers.Reduce));

        dispatcher.RegisterHandler<AddMessage>(action =>
            _store.Dispatch(action, MessagesReducers.Reduce));

        dispatcher.RegisterHandler<UpdateMessage>(action =>
            _store.Dispatch(action, MessagesReducers.Reduce));

        dispatcher.RegisterHandler<RemoveLastMessage>(action =>
            _store.Dispatch(action, MessagesReducers.Reduce));

        dispatcher.RegisterHandler<ClearMessages>(action =>
            _store.Dispatch(action, MessagesReducers.Reduce));
    }


    public MessagesState State => _store.State;


    public IObservable<MessagesState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();
}