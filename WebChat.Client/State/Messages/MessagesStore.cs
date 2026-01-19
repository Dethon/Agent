namespace WebChat.Client.State.Messages;

/// <summary>
/// Store for message state management.
/// Wraps Store&lt;MessagesState&gt; and registers action handlers with the Dispatcher.
/// </summary>
public sealed class MessagesStore : IDisposable
{
    private readonly Store<MessagesState> _store;

    public MessagesStore(Dispatcher dispatcher)
    {
        _store = new Store<MessagesState>(MessagesState.Initial);

        // Register handlers for all message actions
        dispatcher.RegisterHandler<LoadMessages>(action =>
            _store.Dispatch(action, MessagesReducers.Reduce));

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

    /// <summary>
    /// Current state value for synchronous reads.
    /// </summary>
    public MessagesState State => _store.State;

    /// <summary>
    /// Observable state stream for subscriptions.
    /// </summary>
    public IObservable<MessagesState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();
}
