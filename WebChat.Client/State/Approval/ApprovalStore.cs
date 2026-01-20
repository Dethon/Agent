namespace WebChat.Client.State.Approval;

public sealed class ApprovalStore : IDisposable
{
    private readonly Store<ApprovalState> _store;

    public ApprovalStore(Dispatcher dispatcher)
    {
        _store = new Store<ApprovalState>(ApprovalState.Initial);

        dispatcher.RegisterHandler<ShowApproval>(action =>
            _store.Dispatch(action, ApprovalReducers.Reduce));
        dispatcher.RegisterHandler<ApprovalResponding>(action =>
            _store.Dispatch(action, ApprovalReducers.Reduce));
        dispatcher.RegisterHandler<ApprovalResolved>(action =>
            _store.Dispatch(action, ApprovalReducers.Reduce));
        dispatcher.RegisterHandler<ClearApproval>(action =>
            _store.Dispatch(action, ApprovalReducers.Reduce));
    }

    public ApprovalState State => _store.State;

    public IObservable<ApprovalState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();
}
