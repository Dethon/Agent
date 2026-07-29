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

    // TelegramBotService only *warns* on this — nothing gates on it: the message is emitted into
    // the inbox either way, because Telegram has no channel-level way to tell the sender "try again
    // later" (see the buffering comment in TelegramBotService.ProcessUpdateAsync). HasSubscribers would
    // still be the wrong signal here — it stays true for up to an hour after a subscriber goes
    // quiet, so the warning would fall silent exactly when the agent has stopped polling.
    public bool HasActiveSessions => inbox.HasLiveSubscriber(ChannelProtocol.LiveSubscriberFreshness);
}