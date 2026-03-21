using Microsoft.Extensions.Logging;

namespace McpChannelSignalR.Services;

public sealed class StubApprovalService(ILogger<StubApprovalService> logger) : IApprovalService
{
    public Task<string> RequestApprovalAsync(string conversationId, string requestsJson)
    {
        logger.LogDebug(
            "RequestApproval: conversation={ConversationId}, requests={Requests}",
            conversationId, requestsJson);
        return Task.FromResult("approved");
    }

    public Task NotifyAutoApprovedAsync(string conversationId, string requestsJson)
    {
        logger.LogDebug(
            "NotifyAutoApproved: conversation={ConversationId}, requests={Requests}",
            conversationId, requestsJson);
        return Task.CompletedTask;
    }

    public Task RespondToApprovalAsync(string approvalId, string result)
    {
        logger.LogDebug("RespondToApproval: id={ApprovalId}, result={Result}", approvalId, result);
        return Task.CompletedTask;
    }
}
