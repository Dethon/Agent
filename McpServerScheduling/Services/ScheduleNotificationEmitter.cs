using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerScheduling.Services;

public sealed class ScheduleNotificationEmitter(ILogger<ScheduleNotificationEmitter> logger)
    : IScheduleNotificationEmitter
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

    public async Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync(
                    ChannelProtocol.MessageNotification, payload, ChannelProtocol.SerializerOptions, ct);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to emit channel/message notification");
                return false;
            }
        });

        var results = await Task.WhenAll(tasks);
        return Array.Exists(results, delivered => delivered);
    }
}