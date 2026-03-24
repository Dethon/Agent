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
    long ToolErrors);

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
}
