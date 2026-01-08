using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients;

public sealed class AutoToolApprovalHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ToolApprovalResult.AutoApproved);
    }

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class AutoApproveToolHandlerFactory : IToolApprovalHandlerFactory
{
    private static readonly AutoToolApprovalHandler _approvalHandler = new();

    public IToolApprovalHandler Create(AgentKey agentKey)
    {
        return _approvalHandler;
    }
}