using Domain.Channels;
using Domain.DTOs.Channel;

namespace McpChannelVoice.Services;

// Mirrors the emitter in the other channels (Telegram/SignalR/ServiceBus): callers pass the
// message fields and the emitter assembles the ChannelMessageNotification (Timestamp included)
// before enqueuing it into the shared ChannelInbox for channel_receive long-pollers. The only
// voice-specific additions are the optional room `location` and `satelliteId`, which ride on the
// shared notification for room-/device-aware prompts.
//
// Left non-sealed/virtual purely as a test seam: CapturingEmitter overrides EmitMessageNotificationAsync
// so the dispatcher's room-awareness behavior can be asserted without a live MCP session.
public class ChannelNotificationEmitter(ChannelInbox inbox)
{
    public bool HasActiveSessions => inbox.HasSubscribers;

    public virtual Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string? agentId,
        string? location,
        string? satelliteId,
        string? dismissedAlert,
        CancellationToken cancellationToken = default)
    {
        inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Location = location,
            SatelliteId = satelliteId,
            DismissedAlert = dismissedAlert,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return Task.CompletedTask;
    }
}