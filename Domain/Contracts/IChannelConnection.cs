using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.Contracts;

[PublicAPI]
public interface IChannelConnection
{
    string ChannelId { get; }

    // Attach-only channels (declared per endpoint in config) cannot own a conversation:
    // their create_conversation hands back the id it is given instead of persisting a
    // topic, so delivery fan-out must never let them anchor the shared conversation id.
    bool AttachOnly { get; }

    IAsyncEnumerable<ChannelMessage> Messages { get; }

    Task SendReplyAsync(
        string conversationId,
        string content,
        ReplyContentType contentType,
        bool isComplete,
        string? messageId,
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
        string? initialPrompt,
        string? address,
        string? existingConversationId,
        CancellationToken ct);
}