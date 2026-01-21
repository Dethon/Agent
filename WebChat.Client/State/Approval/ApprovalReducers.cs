namespace WebChat.Client.State.Approval;

public static class ApprovalReducers
{
    public static ApprovalState Reduce(ApprovalState state, IAction action) => action switch
    {
        ShowApproval show => new ApprovalState
        {
            CurrentRequest = show.Request,
            TopicId = show.TopicId,
            IsResponding = false
        },
        ApprovalResponding => state with
        {
            IsResponding = true
        },
        ApprovalResolved => ApprovalState.Initial,
        ClearApproval => ApprovalState.Initial,
        _ => state
    };
}