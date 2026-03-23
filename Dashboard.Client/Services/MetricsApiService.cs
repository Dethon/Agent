using System.Net.Http.Json;
using Domain.DTOs.Metrics;

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

    public Task<List<ErrorEvent>?> GetErrorsAsync() =>
        http.GetFromJsonAsync<List<ErrorEvent>>("api/metrics/errors");

    public Task<List<ScheduleExecutionEvent>?> GetSchedulesAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ScheduleExecutionEvent>>($"api/metrics/schedules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ServiceHealthResponse>?> GetHealthAsync() =>
        http.GetFromJsonAsync<List<ServiceHealthResponse>>("api/metrics/health");

    public Task<Dictionary<string, long>?> GetTokensByUserAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<Dictionary<string, long>>($"api/metrics/tokens/by-user?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<Dictionary<string, long>?> GetTokensByModelAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<Dictionary<string, long>>($"api/metrics/tokens/by-model?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
}
