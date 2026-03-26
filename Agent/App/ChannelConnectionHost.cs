using Agent.Settings;
using Infrastructure.Clients.Channels;

namespace Agent.App;

public class ChannelConnectionHost(
    ChannelEndpoint[] endpoints,
    IReadOnlyList<IMcpChannelConnection> connections,
    ILogger<ChannelConnectionHost> logger,
    TimeSpan? healthCheckInterval = null) : BackgroundService
{
    private readonly TimeSpan _healthCheckInterval = healthCheckInterval ?? TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpointMap = endpoints.ToDictionary(e => e.ChannelId, e => e.Endpoint);

        var tasks = connections
            .Where(c => endpointMap.ContainsKey(c.ChannelId))
            .Select(conn => MaintainConnectionAsync(conn, endpointMap[conn.ChannelId], stoppingToken));

        await Task.WhenAll(tasks);
    }

    private async Task MaintainConnectionAsync(
        IMcpChannelConnection conn, string endpoint, CancellationToken ct)
    {
        await ConnectWithRetryAsync(conn, endpoint, ct);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_healthCheckInterval, ct);

            if (!await conn.IsHealthyAsync(ct))
            {
                logger.LogWarning("Channel {ChannelId} health check failed, reconnecting", conn.ChannelId);
                await ReconnectWithRetryAsync(conn, endpoint, ct);
            }
        }
    }

    private async Task ConnectWithRetryAsync(
        IMcpChannelConnection conn, string endpoint, CancellationToken ct)
    {
        const int maxDelaySeconds = 30;
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation(
                    "Connecting channel {ChannelId} to {Endpoint} (attempt {Attempt})",
                    conn.ChannelId, endpoint, attempt + 1);
                await conn.ConnectAsync(endpoint, ct);
                logger.LogInformation("Channel {ChannelId} connected", conn.ChannelId);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempt++;
                var delay = Math.Min((int)Math.Pow(2, attempt), maxDelaySeconds);
                logger.LogWarning(
                    "Failed to connect channel {ChannelId} (attempt {Attempt}), retrying in {Delay}s: {Error}",
                    conn.ChannelId, attempt, delay, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
        }
    }

    private async Task ReconnectWithRetryAsync(
        IMcpChannelConnection conn, string endpoint, CancellationToken ct)
    {
        const int maxDelaySeconds = 30;
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                attempt++;
                logger.LogInformation(
                    "Reconnecting channel {ChannelId} to {Endpoint} (attempt {Attempt})",
                    conn.ChannelId, endpoint, attempt);
                await conn.ReconnectAsync(endpoint, ct);
                logger.LogInformation("Channel {ChannelId} reconnected", conn.ChannelId);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delay = Math.Min((int)Math.Pow(2, attempt), maxDelaySeconds);
                logger.LogWarning(
                    "Failed to reconnect channel {ChannelId} (attempt {Attempt}), retrying in {Delay}s: {Error}",
                    conn.ChannelId, attempt, delay, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
        }
    }
}