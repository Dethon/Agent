using Domain.Channels;
using Domain.DTOs.Channel;
using McpChannelVoice.Services;

namespace Tests.Unit.McpChannelVoice;

// Shared test double: records emitted notifications instead of enqueuing into a ChannelInbox
// (the real emitter is a silent no-op when nothing is subscribed).
internal sealed class CapturingEmitter : ChannelNotificationEmitter
{
    public List<ChannelMessageNotification> Captured { get; } = new();

    public CapturingEmitter() : base(new ChannelInbox()) { }

    public override Task EmitMessageNotificationAsync(
        string conversationId, string sender, string content, string? agentId, string? location,
        string? satelliteId, string? dismissedAlert, CancellationToken ct = default)
    {
        Captured.Add(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Location = location,
            SatelliteId = satelliteId,
            DismissedAlert = dismissedAlert
        });
        return Task.CompletedTask;
    }
}