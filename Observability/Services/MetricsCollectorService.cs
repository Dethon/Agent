using System.Text.Json;
using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.SignalR;
using Observability.Hubs;
using StackExchange.Redis;

namespace Observability.Services;

public record ServiceHealthUpdate(string Service, bool IsHealthy, DateTimeOffset Timestamp);

public sealed class MetricsCollectorService(
    IConnectionMultiplexer redis,
    IHubContext<MetricsHub> hubContext,
    ILogger<MetricsCollectorService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan _dailyKeyTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();

        await subscriber.SubscribeAsync(
            RedisChannel.Literal("metrics:events"),
            async void (_, message) =>
            {
                try
                {
                    var evt = JsonSerializer.Deserialize<MetricEvent>((string)message!, _jsonOptions);
                    if (evt is null)
                    {
                        return;
                    }

                    var db = redis.GetDatabase();
                    await ProcessEventAsync(evt, db);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process metric event");
                }
            });

        // Periodically check for services that stopped sending heartbeats
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_healthCheckInterval, stoppingToken);
                await CheckHealthAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        await subscriber.UnsubscribeAsync(RedisChannel.Literal("metrics:events"));
    }

    private async Task CheckHealthAsync()
    {
        try
        {
            var db = redis.GetDatabase();
            var knownServices = await db.SetMembersAsync("metrics:health:known");

            foreach (var member in knownServices)
            {
                var service = member.ToString();
                var isHealthy = await db.KeyExistsAsync($"metrics:health:{service}");
                if (!isHealthy)
                {
                    await hubContext.Clients.All.SendAsync("OnHealthUpdate",
                        new ServiceHealthUpdate(service, false, DateTimeOffset.UtcNow));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check service health");
        }
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
            case MemoryRecallEvent recall:
                await ProcessMemoryRecallAsync(recall, db);
                break;
            case MemoryExtractionEvent extraction:
                await ProcessMemoryExtractionAsync(extraction, db);
                break;
            case MemoryDreamingEvent dreaming:
                await ProcessMemoryDreamingAsync(dreaming, db);
                break;
        }
    }

    private async Task ProcessTokenUsageAsync(TokenUsageEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:tokens:{dateKey}";
        var totalsKey = $"metrics:totals:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "tokens:input", evt.InputTokens),
            db.HashIncrementAsync(totalsKey, "tokens:output", evt.OutputTokens),
            db.HashIncrementAsync(totalsKey, "tokens:cost", (long)(evt.Cost * 10000m)),
            db.HashIncrementAsync(totalsKey, $"tokens:byUser:{evt.Sender}", evt.InputTokens + evt.OutputTokens),
            db.HashIncrementAsync(totalsKey, $"tokens:byModel:{evt.Model}", evt.InputTokens + evt.OutputTokens),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnTokenUsage", evt);
    }

    private async Task ProcessToolCallAsync(ToolCallEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:tools:{dateKey}";
        var totalsKey = $"metrics:totals:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "tools:count"),
            db.HashIncrementAsync(totalsKey, $"tools:byName:{evt.ToolName}"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (!evt.Success)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, "tools:errors"));
        }

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnToolCall", evt);

        if (!evt.Success)
        {
            var errorEvent = new ErrorEvent
            {
                Service = "ToolCall",
                ErrorType = evt.ToolName,
                Message = evt.Error ?? "Tool call failed",
                Timestamp = evt.Timestamp,
                AgentId = evt.AgentId,
                ConversationId = evt.ConversationId
            };
            await ProcessErrorAsync(errorEvent, db);
        }
    }

    private async Task ProcessErrorAsync(ErrorEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:errors:{dateKey}";
        var recentKey = "metrics:errors:recent";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.ListLeftPushAsync(recentKey, json),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await db.ListTrimAsync(recentKey, 0, 99);

        await hubContext.Clients.All.SendAsync("OnError", evt);
    }

    private async Task ProcessScheduleExecutionAsync(ScheduleExecutionEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:schedules:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnScheduleExecution", evt);
    }

    private async Task ProcessHeartbeatAsync(HeartbeatEvent evt, IDatabase db)
    {
        var key = $"metrics:health:{evt.Service}";
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.StringSetAsync(key, json, TimeSpan.FromSeconds(60)),
            db.SetAddAsync("metrics:health:known", evt.Service));

        await hubContext.Clients.All.SendAsync("OnHealthUpdate",
            new ServiceHealthUpdate(evt.Service, true, evt.Timestamp));
    }

    private async Task ProcessMemoryRecallAsync(MemoryRecallEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:memory-recall:{dateKey}";
        var totalsKey = $"metrics:totals:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "memory:recalls"),
            db.HashIncrementAsync(totalsKey, "memory:recallDuration", evt.DurationMs),
            db.HashIncrementAsync(totalsKey, "memory:recallMemories", evt.MemoryCount),
            db.HashIncrementAsync(totalsKey, $"memory:byUser:{evt.UserId}"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnMemoryRecall", evt);
    }

    private async Task ProcessMemoryExtractionAsync(MemoryExtractionEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:memory-extraction:{dateKey}";
        var totalsKey = $"metrics:totals:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "memory:extractions"),
            db.HashIncrementAsync(totalsKey, "memory:extractionDuration", evt.DurationMs),
            db.HashIncrementAsync(totalsKey, "memory:candidates", evt.CandidateCount),
            db.HashIncrementAsync(totalsKey, "memory:stored", evt.StoredCount),
            db.HashIncrementAsync(totalsKey, $"memory:byUser:{evt.UserId}"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnMemoryExtraction", evt);
    }

    private async Task ProcessMemoryDreamingAsync(MemoryDreamingEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:memory-dreaming:{dateKey}";
        var totalsKey = $"metrics:totals:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "memory:dreamings"),
            db.HashIncrementAsync(totalsKey, "memory:merged", evt.MergedCount),
            db.HashIncrementAsync(totalsKey, "memory:decayed", evt.DecayedCount),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (evt.ProfileRegenerated)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, "memory:profileRegens"));
        }

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnMemoryDreaming", evt);
    }
}