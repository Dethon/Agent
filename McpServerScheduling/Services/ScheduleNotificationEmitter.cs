using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerScheduling.Services;

public sealed record SchedulePayload(
    string ConversationId,
    string Sender,
    string Content,
    string AgentId,
    IReadOnlyList<ReplyTarget> ReplyTo,
    MessageOrigin Origin,
    DateTimeOffset Timestamp);

public sealed class ScheduleNotificationEmitter(ILogger<ScheduleNotificationEmitter> logger)
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

    public static SchedulePayload BuildPayload(
        string conversationId, string sender, string content, string agentId,
        IReadOnlyList<ReplyTarget> replyTo, MessageOrigin origin) =>
        new(conversationId, sender, content, agentId, replyTo, origin, DateTimeOffset.UtcNow);

    public async Task EmitAsync(SchedulePayload payload, CancellationToken ct = default)
    {
        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync("notifications/channel/message", payload, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to emit channel/message notification");
            }
        });

        await Task.WhenAll(tasks);
    }
}