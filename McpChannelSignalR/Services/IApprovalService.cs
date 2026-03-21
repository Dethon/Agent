namespace McpChannelSignalR.Services;

public interface IApprovalService
{
    Task<string> RequestApprovalAsync(string conversationId, string requestsJson);
    Task NotifyAutoApprovedAsync(string conversationId, string requestsJson);
    Task RespondToApprovalAsync(string approvalId, string result);
}
