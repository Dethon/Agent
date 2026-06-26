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
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeyTtl = TimeSpan.FromSeconds(60);

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

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (service, url) in targets)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    // Any HTTP response (even non-2xx) means the container is up and listening.
                    using var _ = await http.GetAsync(url, cts.Token);
                    await MarkHealthyAsync(db, service, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "health probe for {Service} at {Url} failed", service, url);
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MarkHealthyAsync(IDatabase db, string service, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await Task.WhenAll(
            db.StringSetAsync($"metrics:health:{service}", now.ToString("o"), KeyTtl),
            db.SetAddAsync("metrics:health:known", service));
        await hubContext.Clients.All.SendAsync(
            "OnHealthUpdate", new ServiceHealthUpdate(service, true, now), ct);
    }
}