using System.Collections.Concurrent;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Agent.Services.SubAgents;

public sealed class SystemChannelConnection : IChannelConnection
{
    public const string Id = "system";

    private readonly Channel<ChannelMessage> _channel =
        Channel.CreateUnbounded<ChannelMessage>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });

    private readonly ConcurrentDictionary<string, IChannelConnection> _replyRoutes = new();

    public string ChannelId => Id;

    public IAsyncEnumerable<ChannelMessage> Messages => _channel.Reader.ReadAllAsync();

    public void InjectAsync(ChannelMessage message, IChannelConnection? replyChannel)
    {
        if (replyChannel is not null)
            _replyRoutes[message.ConversationId] = replyChannel;

        _channel.Writer.TryWrite(message);
    }

    public Task SendReplyAsync(string conversationId, string content, ReplyContentType contentType,
        bool isComplete, string? messageId, CancellationToken ct)
    {
        if (_replyRoutes.TryGetValue(conversationId, out var route))
            return route.SendReplyAsync(conversationId, content, contentType, isComplete, messageId, ct);
        return Task.CompletedTask;
    }

    public Task<ToolApprovalResult> RequestApprovalAsync(string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
    {
        if (_replyRoutes.TryGetValue(conversationId, out var route))
            return route.RequestApprovalAsync(conversationId, requests, ct);
        return Task.FromResult(ToolApprovalResult.Approved);
    }

    public Task NotifyAutoApprovedAsync(string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
    {
        if (_replyRoutes.TryGetValue(conversationId, out var route))
            return route.NotifyAutoApprovedAsync(conversationId, requests, ct);
        return Task.CompletedTask;
    }

    public Task<string?> CreateConversationAsync(string agentId, string topicName, string sender,
        CancellationToken ct) => Task.FromResult<string?>(null);

    public IAsyncEnumerable<SubAgentCancelRequest> SubAgentCancelRequests =>
        AsyncEnumerable.Empty<SubAgentCancelRequest>();
}
