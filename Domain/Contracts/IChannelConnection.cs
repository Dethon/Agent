using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.Contracts;

[PublicAPI]
public interface IChannelConnection
{
    string ChannelId { get; }

    IAsyncEnumerable<ChannelMessage> Messages { get; }

    Task SendReplyAsync(
        string conversationId,
        string content,
        string contentType,
        bool isComplete,
        CancellationToken ct);

    Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct);

    Task NotifyAutoApprovedAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct);

    Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        CancellationToken ct);
}
