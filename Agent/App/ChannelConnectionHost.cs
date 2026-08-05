using Agent.Settings;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;

namespace Agent.App;

// What the agent knows that a connection does not: which channels have an endpoint to dial, and
// which endpoint each one is. Everything after that — connect, register, watch, reconnect,
// re-register — is the connection's own run.
public class ChannelConnectionHost(
    ChannelEndpoint[] endpoints,
    IReadOnlyList<IMcpChannelConnection> connections,
    IReadOnlyList<AgentCatalogEntry> agentCatalog,
    ILogger<ChannelConnectionHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpointMap = endpoints.ToDictionary(e => e.ChannelId, e => e.Endpoint);

        var connectionIds = connections.Select(c => c.ChannelId).ToHashSet();
        foreach (var orphan in endpoints.Where(e => !connectionIds.Contains(e.ChannelId)))
        {
            logger.LogWarning(
                "Endpoint {ChannelId} ({Endpoint}) matches no registered channel connection and will never be run",
                orphan.ChannelId, orphan.Endpoint);
        }

        var runs = connections
            .Where(c => endpointMap.ContainsKey(c.ChannelId))
            .Select(conn =>
            {
                var endpoint = endpointMap[conn.ChannelId];
                logger.LogInformation("Running channel {ChannelId} against {Endpoint}", conn.ChannelId, endpoint);
                return conn.RunAsync(endpoint, agentCatalog, stoppingToken);
            });

        await Task.WhenAll(runs);
    }
}