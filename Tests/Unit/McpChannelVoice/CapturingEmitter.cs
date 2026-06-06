using Domain.DTOs.Channel;
using McpChannelVoice.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Unit.McpChannelVoice;

// Shared test double: records emitted notifications instead of dispatching to MCP sessions
// (the real emitter is a silent no-op when no sessions are registered).
internal sealed class CapturingEmitter : ChannelNotificationEmitter
{
    public List<ChannelMessageNotification> Captured { get; } = new();

    public CapturingEmitter() : base(NullLogger<ChannelNotificationEmitter>.Instance) { }

    public override Task EmitMessageNotificationAsync(
        string conversationId, string sender, string content, string? agentId, string? location,
        string? satelliteId, CancellationToken ct = default)
    {
        Captured.Add(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Location = location,
            SatelliteId = satelliteId
        });
        return Task.CompletedTask;
    }
}