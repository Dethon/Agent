using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;

namespace Infrastructure.Clients.ToolApproval;

public sealed class WebToolApprovalHandler(
    WebChatMessengerClient messengerClient,
    string topicId) : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        return messengerClient.RequestApprovalAsync(topicId, requests, cancellationToken);
    }

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        return messengerClient.NotifyAutoApprovedAsync(topicId, requests, cancellationToken);
    }
}

public sealed class WebToolApprovalHandlerFactory(WebChatMessengerClient messengerClient) : IToolApprovalHandlerFactory
{
    public IToolApprovalHandler Create(AgentKey agentKey)
    {
        var topicId = messengerClient.GetTopicIdByChatId(agentKey.ChatId)
                      ?? throw new InvalidOperationException(
                          $"No active topic found for chatId {agentKey.ChatId}");

        return new WebToolApprovalHandler(messengerClient, topicId);
    }
}