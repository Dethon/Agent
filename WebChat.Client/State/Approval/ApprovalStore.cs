using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Approval;

public record ShowApproval(string TopicId, ToolApprovalRequestMessage Request) : IAction;

public record ApprovalResolved(string ApprovalId, string? ToolCalls) : IAction;

public record ClearApproval : IAction;

public sealed class ApprovalStore : IDisposable
{
    private readonly Store<ApprovalState> _store;

    public ApprovalStore(Dispatcher dispatcher)
    {
        _store = new Store<ApprovalState>(ApprovalState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public ApprovalState State => _store.State;

    public IObservable<ApprovalState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static ApprovalState Reduce(ApprovalState state, IAction action) => action switch
    {
        ShowApproval show => new ApprovalState
        {
            CurrentRequest = show.Request,
            TopicId = show.TopicId
        },
        ApprovalResolved => ApprovalState.Initial,
        ClearApproval => ApprovalState.Initial,
        _ => state
    };
}