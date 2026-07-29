using Domain.Channels;
using Domain.DTOs.Channel;

namespace McpChannelServiceBus.Services;

public sealed class ChannelNotificationEmitter(ChannelInbox inbox)
{
    public Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return Task.CompletedTask;
    }

    public bool HasActiveSessions => inbox.HasSubscribers;
}