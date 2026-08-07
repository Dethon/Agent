using System.Collections.Immutable;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Approval;

public record ShowApproval(string TopicId, ToolApprovalRequestMessage Request) : IAction;

public record ApprovalResolved(string ApprovalId) : IAction;

public record ClearApproval(string ApprovalId) : IAction;

// What the server says is still pending for one conversation, which is the whole truth about
// it: anything else this client holds for that conversation was resolved or timed out while it
// was away, and a deleted conversation leaves nothing pending at all.
public record TopicApprovalsReconciled(string TopicId, ToolApprovalRequestMessage? StillPending) : IAction;

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
        ShowApproval show => state with { Pending = Queue(state.Pending, show.TopicId, show.Request) },
        ApprovalResolved resolved => state with { Pending = Drop(state.Pending, resolved.ApprovalId) },
        ClearApproval clear => state with { Pending = Drop(state.Pending, clear.ApprovalId) },
        TopicApprovalsReconciled reconciled => state with { Pending = Reconcile(state.Pending, reconciled) },
        _ => state
    };

    private static ImmutableList<PendingApproval> Queue(
        ImmutableList<PendingApproval> pending, string topicId, ToolApprovalRequestMessage request) =>
        pending.Any(p => p.Request.ApprovalId == request.ApprovalId)
            ? pending
            : pending.Add(new PendingApproval(topicId, request));

    private static ImmutableList<PendingApproval> Drop(
        ImmutableList<PendingApproval> pending, string approvalId) =>
        pending.RemoveAll(p => p.Request.ApprovalId == approvalId);

    private static ImmutableList<PendingApproval> Reconcile(
        ImmutableList<PendingApproval> pending, TopicApprovalsReconciled reconciled)
    {
        var kept = pending.RemoveAll(p =>
            p.TopicId == reconciled.TopicId &&
            p.Request.ApprovalId != reconciled.StillPending?.ApprovalId);

        return reconciled.StillPending is null
            ? kept
            : Queue(kept, reconciled.TopicId, reconciled.StillPending);
    }
}