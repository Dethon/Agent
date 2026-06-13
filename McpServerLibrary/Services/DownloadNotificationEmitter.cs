using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerLibrary.Services;

public sealed class DownloadNotificationEmitter(ILogger<DownloadNotificationEmitter> logger)
    : IDownloadNotificationEmitter
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

    // Tool sessions (the agent's per-conversation MCP clients) also land here; they silently drop
    // channel/message notifications, so only channel-client sessions count as delivery targets —
    // otherwise EmitAsync reports success, the routing entry is deleted, and the alert is lost.
    public bool HasActiveSessions =>
        _activeSessions.Values.Any(s => ChannelProtocol.IsChannelClientName(s.ClientInfo?.Name));

    public async Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        var channelSessions = _activeSessions.Values
            .Where(s => ChannelProtocol.IsChannelClientName(s.ClientInfo?.Name))
            .ToList();
        if (channelSessions.Count == 0)
        {
            return false;
        }

        var tasks = channelSessions.Select(async server =>
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