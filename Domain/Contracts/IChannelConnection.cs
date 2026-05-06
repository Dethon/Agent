using Domain.DTOs;
using Domain.DTOs.SubAgent;
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
        CancellationToken ct);

    IAsyncEnumerable<SubAgentCancelRequest> SubAgentCancelRequests
        => AsyncEnumerable.Empty<SubAgentCancelRequest>();

    Task AnnounceSubAgentStartAsync(string conversationId, string handle, string subAgentId,
        CancellationToken ct) => Task.CompletedTask;

    Task UpdateSubAgentStatusAsync(string conversationId, string handle, string status,
        CancellationToken ct) => Task.CompletedTask;
}