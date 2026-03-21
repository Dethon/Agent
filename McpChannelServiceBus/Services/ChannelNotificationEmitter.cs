using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpChannelServiceBus.Services;

public sealed class ChannelNotificationEmitter(ILogger<ChannelNotificationEmitter> logger)
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

    public async Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        };

        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync(
                    "notifications/channel/message",
                    payload,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to emit channel/message notification to MCP session");
            }
        });

        await Task.WhenAll(tasks);
    }

    public bool HasActiveSessions => !_activeSessions.IsEmpty;
}
