using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using JetBrains.Annotations;
using StackExchange.Redis;

namespace Observability.Services;

[UsedImplicitly] 
public record ServiceHealthResult(string Service, bool IsHealthy, string LastSeen);

public record MetricsSummary(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    decimal Cost,
    long ToolCalls,
    long ToolErrors);

public sealed class MetricsQueryService(IConnectionMultiplexer redis)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<MetricsSummary> GetSummaryAsync(DateOnly from, DateOnly to)
    {
        var db = redis.GetDatabase();
        long inputTokens = 0, outputTokens = 0, costFixed = 0, toolCalls = 0, toolErrors = 0;

        foreach (var date in EnumerateDates(from, to))
        {
            var key = $"metrics:totals:{date:yyyy-MM-dd}";
            var entries = await db.HashGetAllAsync(key);
            foreach (var entry in entries)
            {
                var field = entry.Name.ToString();
                var value = (long)entry.Value;
                switch (field)
                {
                    case "tokens:input": inputTokens += value; break;
                    case "tokens:output": outputTokens += value; break;
                    case "tokens:cost": costFixed += value; break;
                    case "tools:count": toolCalls += value; break;
                    case "tools:errors": toolErrors += value; break;
                }
            }
        }

        return new MetricsSummary(
            inputTokens,
            outputTokens,
            inputTokens + outputTokens,
            costFixed / 10000m,
            toolCalls,
            toolErrors);
    }

    public async Task<IReadOnlyList<T>> GetEventsAsync<T>(string keyPrefix, DateOnly from, DateOnly to)
        where T : MetricEvent
    {
        var db = redis.GetDatabase();
        var results = new List<T>();

        foreach (var date in EnumerateDates(from, to))
        {
            var key = $"{keyPrefix}{date:yyyy-MM-dd}";
            var entries = await db.SortedSetRangeByScoreAsync(key);
            results.AddRange(entries
                .Select(e => JsonSerializer.Deserialize<MetricEvent>(e.ToString(), _jsonOptions))
                .OfType<T>());
        }

        return results;
    }

    public async Task<IReadOnlyList<ErrorEvent>> GetRecentErrorsAsync(int limit = 100)
    {
        var db = redis.GetDatabase();
        var entries = await db.ListRangeAsync("metrics:errors:recent", 0, limit - 1);

        return entries
            .Select(e => JsonSerializer.Deserialize<MetricEvent>(e.ToString(), _jsonOptions))
            .OfType<ErrorEvent>()
            .ToList();
    }

    public async Task<IReadOnlyList<ServiceHealthResult>> GetHealthAsync()
    {
        var db = redis.GetDatabase();
        var knownServices = await db.SetMembersAsync("metrics:health:known");

        var tasks = knownServices.Select(async member =>
        {
            var service = member.ToString();
            var value = await db.StringGetAsync($"metrics:health:{service}");
            var isHealthy = value.HasValue;
            return new ServiceHealthResult(service, isHealthy, isHealthy ? value.ToString() : "N/A");
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    public async Task<Dictionary<string, long>> GetTokenBreakdownAsync(
        string prefix, DateOnly from, DateOnly to)
    {
        var db = redis.GetDatabase();
        var breakdown = new Dictionary<string, long>();

        foreach (var date in EnumerateDates(from, to))
        {
            var key = $"metrics:totals:{date:yyyy-MM-dd}";
            var entries = await db.HashGetAllAsync(key);
            foreach (var entry in entries.Where(e => e.Name.ToString().StartsWith(prefix)))
            {
                var name = entry.Name.ToString()[prefix.Length..];
                var value = (long)entry.Value;
                if (!breakdown.TryAdd(name, value))
                {
                    breakdown[name] += value;
                }
            }
        }

        return breakdown;
    }

    public async Task<Dictionary<string, decimal>> GetTokenGroupedAsync(
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<TokenUsageEvent>("metrics:tokens:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                TokenDimension.User => e.Sender,
                TokenDimension.Model => e.Model,
                TokenDimension.Agent => e.AgentId ?? "unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    TokenMetric.Tokens => g.Sum(e => (decimal)(e.InputTokens + e.OutputTokens)),
                    TokenMetric.Cost => g.Sum(e => e.Cost),
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    public async Task<Dictionary<string, decimal>> GetToolGroupedAsync(
        ToolDimension dimension, ToolMetric metric, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<ToolCallEvent>("metrics:tools:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                ToolDimension.ToolName => e.ToolName,
                ToolDimension.Status => e.Success ? "Success" : "Failure",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    ToolMetric.CallCount => g.Count(),
                    ToolMetric.AvgDuration => (decimal)g.Average(e => e.DurationMs),
                    ToolMetric.ErrorRate => g.Any()
                        ? (decimal)g.Count(e => !e.Success) / g.Count() * 100m
                        : 0m,
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    public async Task<Dictionary<string, int>> GetErrorGroupedAsync(
        ErrorDimension dimension, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<ErrorEvent>("metrics:errors:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                ErrorDimension.Service => e.Service,
                ErrorDimension.ErrorType => e.ErrorType,
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<Dictionary<string, int>> GetScheduleGroupedAsync(
        ScheduleDimension dimension, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<ScheduleExecutionEvent>("metrics:schedules:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                ScheduleDimension.Schedule => e.ScheduleId,
                ScheduleDimension.Status => e.Success ? "Success" : "Failure",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static IEnumerable<DateOnly> EnumerateDates(DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            yield return date;
        }
    }
}
