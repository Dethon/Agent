using System.Text.Json;
using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.SignalR;
using Observability.Hubs;
using StackExchange.Redis;

namespace Observability.Services;

public sealed class MetricsCollectorService(
    IConnectionMultiplexer redis,
    IHubContext<MetricsHub> hubContext,
    ILogger<MetricsCollectorService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan DailyKeyTtl = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();

        await subscriber.SubscribeAsync(
            RedisChannel.Literal("metrics:events"),
            async (_, message) =>
            {
                try
                {
                    var evt = JsonSerializer.Deserialize<MetricEvent>((string)message!, JsonOptions);
                    if (evt is null) return;

                    var db = redis.GetDatabase();
                    await ProcessEventAsync(evt, db);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process metric event");
                }
            });

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        await subscriber.UnsubscribeAsync(RedisChannel.Literal("metrics:events"));
    }

    internal async Task ProcessEventAsync(MetricEvent evt, IDatabase db)
    {
        switch (evt)
        {
            case TokenUsageEvent token:
                await ProcessTokenUsageAsync(token, db);
                break;
            case ToolCallEvent tool:
                await ProcessToolCallAsync(tool, db);
                break;
            case ErrorEvent error:
                await ProcessErrorAsync(error, db);
                break;
            case ScheduleExecutionEvent schedule:
                await ProcessScheduleExecutionAsync(schedule, db);
                break;
            case HeartbeatEvent heartbeat:
                await ProcessHeartbeatAsync(heartbeat, db);
                break;
        }
    }

    private async Task ProcessTokenUsageAsync(TokenUsageEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:tokens:{dateKey}";
        var totalsKey = $"metrics:totals:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, JsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "tokens:input", evt.InputTokens),
            db.HashIncrementAsync(totalsKey, "tokens:output", evt.OutputTokens),
            db.HashIncrementAsync(totalsKey, "tokens:cost", (long)(evt.Cost * 10000m)),
            db.HashIncrementAsync(totalsKey, $"tokens:byUser:{evt.Sender}", evt.InputTokens + evt.OutputTokens),
            db.HashIncrementAsync(totalsKey, $"tokens:byModel:{evt.Model}", evt.InputTokens + evt.OutputTokens),
            db.KeyExpireAsync(sortedSetKey, DailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, DailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnTokenUsage", evt);
    }

    private async Task ProcessToolCallAsync(ToolCallEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:tools:{dateKey}";
        var totalsKey = $"metrics:totals:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, JsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "tools:count", 1),
            db.HashIncrementAsync(totalsKey, $"tools:byName:{evt.ToolName}", 1),
            db.KeyExpireAsync(sortedSetKey, DailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, DailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (!evt.Success)
            tasks.Add(db.HashIncrementAsync(totalsKey, "tools:errors", 1));

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnToolCall", evt);
    }

    private async Task ProcessErrorAsync(ErrorEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:errors:{dateKey}";
        var recentKey = "metrics:errors:recent";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, JsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.ListLeftPushAsync(recentKey, json),
            db.KeyExpireAsync(sortedSetKey, DailyKeyTtl, ExpireWhen.HasNoExpiry));

        await db.ListTrimAsync(recentKey, 0, 99);

        await hubContext.Clients.All.SendAsync("OnError", evt);
    }

    private async Task ProcessScheduleExecutionAsync(ScheduleExecutionEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:schedules:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, JsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.KeyExpireAsync(sortedSetKey, DailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnScheduleExecution", evt);
    }

    private async Task ProcessHeartbeatAsync(HeartbeatEvent evt, IDatabase db)
    {
        var key = $"metrics:health:{evt.Service}";
        var json = JsonSerializer.Serialize<MetricEvent>(evt, JsonOptions);

        await db.StringSetAsync(key, json, TimeSpan.FromSeconds(60));

        await hubContext.Clients.All.SendAsync("OnHealthUpdate", evt);
    }
}
