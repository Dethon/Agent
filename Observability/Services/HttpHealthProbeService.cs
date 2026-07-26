using Microsoft.AspNetCore.SignalR;
using Observability.Hubs;
using StackExchange.Redis;

namespace Observability.Services;

public sealed class HttpHealthProbeService(
    IHttpClientFactory httpClientFactory,
    IConnectionMultiplexer redis,
    IHubContext<MetricsHub> hubContext,
    IConfiguration configuration,
    ILogger<HttpHealthProbeService> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _keyTtl = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targets = configuration.GetSection("HttpProbes").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => (Service: c.Key, Url: c.Value!))
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var http = httpClientFactory.CreateClient();
        var db = redis.GetDatabase();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var (service, url) in targets)
                {
                    await ProbeAsync(http, db, service, url, stoppingToken);
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }

    internal async Task ProbeAsync(
        HttpClient http, IDatabase db, string service, string url, CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;

        try
        {
            // Roster registration is unconditional: a configured target keeps its dashboard tile
            // while it is unreachable, so a service that is down reads as red rather than vanishing.
            // It stays inside the try because an escaping exception would stop the whole host.
            await ServiceHealthRegistry.MarkSeenAsync(db, service, now);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            // Any HTTP response (even non-2xx) means the container is up and listening.
            using var _ = await http.GetAsync(url, cts.Token);
            await MarkHealthyAsync(db, service, now, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "health probe for {Service} at {Url} failed", service, url);
        }
    }

    private async Task MarkHealthyAsync(IDatabase db, string service, DateTimeOffset now, CancellationToken ct)
    {
        await db.StringSetAsync($"metrics:health:{service}", now.ToString("o"), _keyTtl);
        await hubContext.Clients.All.SendAsync(
            "OnHealthUpdate", new ServiceHealthUpdate(service, true, now), ct);
    }
}