using Domain.Channels;
using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public sealed class ChannelNotificationEmitter(ChannelInbox inbox)
{
    public Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string agentId,
        AgentConfigPatch? configPatch = null,
        CancellationToken cancellationToken = default)
    {
        inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            ConfigPatch = configPatch,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return Task.CompletedTask;
    }

    public Task EmitCancelNotificationAsync(
        string conversationId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        inbox.Enqueue(ChannelInboxItem.ForCancel(new ChannelCancelNotification
        {
            ConversationId = conversationId,
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return Task.CompletedTask;
    }

    // Not consumed in production today (no caller gates a destructive action on it here), but kept
    // computed the same way as every other migrated channel's HasActiveSessions — see
    // ChannelProtocol.LiveSubscriberFreshness's doc comment for why HasSubscribers is the wrong
    // check wherever this property does end up read.
    public bool HasActiveSessions => inbox.HasLiveSubscriber(ChannelProtocol.LiveSubscriberFreshness);
}