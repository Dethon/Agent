using Domain.Channels;
using Domain.DTOs.Channel;

namespace McpServerScheduling.Services;

public sealed class ScheduleNotificationEmitter(ChannelInbox inbox) : IScheduleNotificationEmitter
{
    // Only the agent's channel connection ever calls channel_receive on this dual-role server —
    // tool sessions (per-conversation filesystem clients) never poll it, so no client-name filter
    // is needed here: the distinction is structural now, not something the emitter enforces.
    //
    // ChannelProtocol.LiveSubscriberFreshness (not HasSubscribers) gates both properties below:
    // ScheduleDispatcherService deletes/advances a schedule only when EmitAsync returns true, so a
    // disconnected-but-still-buffering subscriber must not read as "delivered" — see the constant's
    // own doc comment for why HasSubscribers is the wrong check for that.
    public bool HasActiveSessions => inbox.HasLiveSubscriber(ChannelProtocol.LiveSubscriberFreshness);

    public static ChannelMessageNotification BuildPayload(
        string conversationId, string sender, string content, string agentId,
        IReadOnlyList<ReplyTarget> replyTo, MessageOrigin origin) =>
        new()
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            ReplyTo = replyTo,
            Origin = origin,
            Timestamp = DateTimeOffset.UtcNow
        };

    public Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        if (!inbox.HasLiveSubscriber(ChannelProtocol.LiveSubscriberFreshness))
        {
            return Task.FromResult(false);
        }

        inbox.Enqueue(ChannelInboxItem.ForMessage(payload));
        return Task.FromResult(true);
    }
}