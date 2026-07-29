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

    // ServiceBusProcessorService gates message settlement on this — see
    // ChannelProtocol.LiveSubscriberFreshness's doc comment for why HasSubscribers would be wrong
    // here: it stays true for up to an hour after a subscriber goes quiet, which would complete
    // (settle) the broker message instead of abandoning it, defeating Service Bus's at-least-once
    // redelivery for an item that can still be lost with the in-process inbox.
    public bool HasActiveSessions => inbox.HasLiveSubscriber(ChannelProtocol.LiveSubscriberFreshness);
}