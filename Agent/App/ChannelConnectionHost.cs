using Agent.Settings;
using Infrastructure.Clients.Channels;

namespace Agent.App;

public class ChannelConnectionHost(
    ChannelEndpoint[] endpoints,
    IReadOnlyList<McpChannelConnection> connections,
    ILogger<ChannelConnectionHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpointMap = endpoints.ToDictionary(e => e.ChannelId, e => e.Endpoint);

        var tasks = connections
            .Where(c => endpointMap.ContainsKey(c.ChannelId))
            .Select(async conn =>
            {
                var endpoint = endpointMap[conn.ChannelId];
                try
                {
                    logger.LogInformation(
                        "Connecting channel {ChannelId} to {Endpoint}",
                        conn.ChannelId, endpoint);
                    await conn.ConnectAsync(endpoint, stoppingToken);
                    logger.LogInformation("Channel {ChannelId} connected", conn.ChannelId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex,
                        "Failed to connect channel {ChannelId} to {Endpoint}",
                        conn.ChannelId, endpoint);
                }
            });

        await Task.WhenAll(tasks);
    }
}
