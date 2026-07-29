using Domain.Channels;
using Domain.DTOs.Channel;

namespace McpChannelTelegram.Services;

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

    // TelegramBotService gates its "agent unavailable" drop path on this — see
    // ChannelProtocol.LiveSubscriberFreshness's doc comment for why HasSubscribers would be wrong
    // here: it stays true for up to an hour after a subscriber goes quiet, which would silently
    // accept a message into a buffer nobody is polling instead of dropping it with a clear signal.
    public bool HasActiveSessions => inbox.HasLiveSubscriber(ChannelProtocol.LiveSubscriberFreshness);
}