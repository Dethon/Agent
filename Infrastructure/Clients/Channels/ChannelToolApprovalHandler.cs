using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients.Channels;

public sealed class ChannelToolApprovalHandler(
    IChannelConnection channel,
    string conversationId) : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
        => channel.RequestApprovalAsync(conversationId, requests, cancellationToken);

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
        => channel.NotifyAutoApprovedAsync(conversationId, requests, cancellationToken);
}
