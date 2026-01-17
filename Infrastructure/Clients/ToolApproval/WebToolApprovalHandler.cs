using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;

namespace Infrastructure.Clients.ToolApproval;

public sealed class WebToolApprovalHandler(
    WebChatApprovalManager approvalManager,
    string topicId) : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        return approvalManager.RequestApprovalAsync(topicId, requests, cancellationToken);
    }

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        return approvalManager.NotifyAutoApprovedAsync(topicId, requests, cancellationToken);
    }
}

public sealed class WebToolApprovalHandlerFactory(
    WebChatApprovalManager approvalManager,
    WebChatSessionManager sessionManager) : IToolApprovalHandlerFactory
{
    public IToolApprovalHandler Create(AgentKey agentKey)
    {
        var topicId = sessionManager.GetTopicIdByChatId(agentKey.ChatId)
                      ?? throw new InvalidOperationException(
                          $"No active topic found for chatId {agentKey.ChatId}");

        return new WebToolApprovalHandler(approvalManager, topicId);
    }
}