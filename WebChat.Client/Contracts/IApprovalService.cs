using Domain.DTOs;
using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IApprovalService
{
    Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result);
    Task<ToolApprovalRequestMessage?> GetPendingApprovalForTopicAsync(string topicId);
}