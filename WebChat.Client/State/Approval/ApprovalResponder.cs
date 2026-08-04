using Domain.DTOs;
using WebChat.Client.Contracts;
using WebChat.Client.State.Toast;

namespace WebChat.Client.State.Approval;

// The approval modal's decision, out of the view so it can be tested. The other user actions
// reach the hub through an effect; this one is awaited by the component so its buttons can
// stay disabled until the answer is in, which an action dispatch could not give it.
public sealed class ApprovalResponder(IApprovalService approvalService, IDispatcher dispatcher)
{
    public async Task RespondAsync(string approvalId, ToolApprovalResult result)
    {
        var answered = await approvalService.RespondToApprovalAsync(approvalId, result);

        // A server answering false is live and has answered — the approval is no longer
        // pending, so the prompt goes. Only a call that could not be made leaves it up.
        if (!answered.IsLive)
        {
            dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        dispatcher.Dispatch(new ClearApproval());
    }
}