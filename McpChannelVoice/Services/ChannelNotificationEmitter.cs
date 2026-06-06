using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.Services;

// Mirrors the emitter in the other channels (Telegram/SignalR/ServiceBus): callers pass the
// message fields and the emitter assembles the ChannelMessageNotification (Timestamp included)
// before fanning it out to every active MCP session. The only voice-specific additions are the
// optional room `location` and `satelliteId`, which ride on the shared notification for
// room-/device-aware prompts.
//
// Left non-sealed/virtual purely as a test seam: CapturingEmitter overrides EmitMessageNotificationAsync
// so the dispatcher's room-awareness behavior can be asserted without a live MCP session.
public class ChannelNotificationEmitter(ILogger<ChannelNotificationEmitter> logger)
{
    private readonly ConcurrentDictionary<string, McpServer> _activeSessions = new();

    public void RegisterSession(string sessionId, McpServer server)
    {
        _activeSessions[sessionId] = server;
        logger.LogInformation("MCP session registered: {SessionId}", sessionId);
    }

    public void UnregisterSession(string sessionId)
    {
        _activeSessions.TryRemove(sessionId, out _);
        logger.LogInformation("MCP session unregistered: {SessionId}", sessionId);
    }

    public bool HasActiveSessions => !_activeSessions.IsEmpty;

    public virtual async Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string? agentId,
        string? location,
        string? satelliteId,
        CancellationToken cancellationToken = default)
    {
        var payload = new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Location = location,
            SatelliteId = satelliteId,
            Timestamp = DateTimeOffset.UtcNow
        };

        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync(
                    ChannelProtocol.MessageNotification,
                    payload,
                    ChannelProtocol.SerializerOptions,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to emit channel/message notification");
            }
        });

        await Task.WhenAll(tasks);
    }
}