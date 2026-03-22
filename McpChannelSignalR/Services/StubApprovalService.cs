using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public sealed class StubApprovalService(ILogger<StubApprovalService> logger) : IApprovalService
{
    public Task<string> RequestApprovalAsync(RequestApprovalParams p)
    {
        logger.LogDebug(
            "RequestApproval: conversation={ConversationId}, requests={Requests}",
            p.ConversationId, p.Requests);
        return Task.FromResult("approved");
    }

    public Task NotifyAutoApprovedAsync(RequestApprovalParams p)
    {
        logger.LogDebug(
            "NotifyAutoApproved: conversation={ConversationId}, requests={Requests}",
            p.ConversationId, p.Requests);
        return Task.CompletedTask;
    }

    public Task RespondToApprovalAsync(string approvalId, string result)
    {
        logger.LogDebug("RespondToApproval: id={ApprovalId}, result={Result}", approvalId, result);
        return Task.CompletedTask;
    }
}
