using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public interface IApprovalService
{
    Task<string> RequestApprovalAsync(RequestApprovalParams p);
    Task NotifyAutoApprovedAsync(RequestApprovalParams p);
    Task RespondToApprovalAsync(string approvalId, string result);
}
