using System.Text.Json;
using Domain.DTOs.Metrics;
using StackExchange.Redis;

namespace Observability.Services;

public record MetricsSummary(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    decimal Cost,
    long ToolCalls,
    long ToolErrors);

public sealed class MetricsQueryService(IConnectionMultiplexer redis)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
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
                .Select(e => JsonSerializer.Deserialize<MetricEvent>(e.ToString(), JsonOptions))
                .OfType<T>());
        }

        return results;
    }

    public async Task<IReadOnlyList<ErrorEvent>> GetRecentErrorsAsync(int limit = 100)
    {
        var db = redis.GetDatabase();
        var entries = await db.ListRangeAsync("metrics:errors:recent", 0, limit - 1);

        return entries
            .Select(e => JsonSerializer.Deserialize<MetricEvent>(e.ToString(), JsonOptions))
            .OfType<ErrorEvent>()
            .ToList();
    }

    public async Task<IReadOnlyList<object>> GetHealthAsync()
    {
        var db = redis.GetDatabase();
        var server = redis.GetServers().First();
        var keys = server.Keys(pattern: "metrics:health:*").ToList();

        var tasks = keys.Select(async key =>
        {
            var value = await db.StringGetAsync(key);
            return value.HasValue
                ? JsonSerializer.Deserialize<HeartbeatEvent>(value.ToString(), JsonOptions) as object
                : null;
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).ToList()!;
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
                    breakdown[name] += value;
            }
        }

        return breakdown;
    }

    private static IEnumerable<DateOnly> EnumerateDates(DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
            yield return date;
    }
}
