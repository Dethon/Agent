using System.Net.Http.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.Services;

public record MetricsSummary(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    decimal Cost,
    long ToolCalls,
    long ToolErrors,
    long TotalRecalls = 0,
    long TotalExtractions = 0,
    long TotalDreamings = 0,
    long MemoriesStored = 0,
    long MemoriesMerged = 0,
    long MemoriesDecayed = 0);

public record ServiceHealthResponse(string Service, bool IsHealthy, string LastSeen);

public sealed class MetricsApiService(HttpClient http)
{
    public Task<MetricsSummary?> GetSummaryAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<MetricsSummary>($"api/metrics/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<TokenUsageEvent>?> GetTokensAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<TokenUsageEvent>>($"api/metrics/tokens?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ToolCallEvent>?> GetToolsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ToolCallEvent>>($"api/metrics/tools?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ErrorEvent>?> GetErrorsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ErrorEvent>>($"api/metrics/errors/range?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ScheduleExecutionEvent>?> GetSchedulesAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ScheduleExecutionEvent>>($"api/metrics/schedules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ServiceHealthResponse>?> GetHealthAsync() =>
        http.GetFromJsonAsync<List<ServiceHealthResponse>>("api/metrics/health");

    public Task<Dictionary<string, decimal>?> GetTokenGroupedAsync(
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<Dictionary<string, decimal>>(
            $"api/metrics/tokens/by/{dimension}?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    public Task<Dictionary<string, decimal>?> GetToolGroupedAsync(
        ToolDimension dimension, ToolMetric metric, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<Dictionary<string, decimal>>(
            $"api/metrics/tools/by/{dimension}?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    public Task<Dictionary<string, int>?> GetErrorGroupedAsync(
        ErrorDimension dimension, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<Dictionary<string, int>>(
            $"api/metrics/errors/by/{dimension}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    public Task<Dictionary<string, int>?> GetScheduleGroupedAsync(
        ScheduleDimension dimension, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<Dictionary<string, int>>(
            $"api/metrics/schedules/by/{dimension}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    public Task<List<MemoryRecallEvent>?> GetMemoryRecallAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<MemoryRecallEvent>>($"api/metrics/memory/recall?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<MemoryExtractionEvent>?> GetMemoryExtractionAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<MemoryExtractionEvent>>($"api/metrics/memory/extraction?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<MemoryDreamingEvent>?> GetMemoryDreamingAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<MemoryDreamingEvent>>($"api/metrics/memory/dreaming?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<Dictionary<string, decimal>?> GetMemoryGroupedAsync(
        MemoryDimension dimension, MemoryMetric metric, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<Dictionary<string, decimal>>(
            $"api/metrics/memory/by/{dimension}?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    public Task<List<LatencyEvent>?> GetLatencyAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<LatencyEvent>>($"api/metrics/latency?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<Dictionary<string, decimal>?> GetLatencyGroupedAsync(
        LatencyDimension dimension, LatencyMetric metric, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<Dictionary<string, decimal>>(
            $"api/metrics/latency/by/{dimension}?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    public Task<List<LatencyTrendSeries>?> GetLatencyTrendAsync(
        LatencyMetric metric, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<LatencyTrendSeries>>(
            $"api/metrics/latency/trend?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);
}