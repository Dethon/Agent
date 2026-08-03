using Domain.DTOs;

namespace Domain.Contracts;

public interface IToolApprovalHandler
{
    Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken);

    Task NotifyAutoApprovedAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken);
}