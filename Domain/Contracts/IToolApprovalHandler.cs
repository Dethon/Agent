using Domain.DTOs;

namespace Domain.Contracts;

public interface IToolApprovalHandler
{
    Task<ToolApprovalResult> RequestApprovalAsync(IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken);

    Task NotifyAutoApprovedAsync(IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken);
}