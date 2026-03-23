using Domain.DTOs.Metrics;
using Observability.Services;

namespace Observability;

public static class MetricsApiEndpoints
{
    public static void MapMetricsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/metrics");

        api.MapGet("/summary", async (
            MetricsQueryService query,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetSummaryAsync(fromDate, toDate);
        });

        api.MapGet("/tokens", async (
            MetricsQueryService query,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetEventsAsync<TokenUsageEvent>("metrics:tokens:", fromDate, toDate);
        });

        api.MapGet("/tools", async (
            MetricsQueryService query,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetEventsAsync<ToolCallEvent>("metrics:tools:", fromDate, toDate);
        });

        api.MapGet("/errors", async (
            MetricsQueryService query,
            int? limit) =>
        {
            return await query.GetRecentErrorsAsync(limit ?? 100);
        });

        api.MapGet("/schedules", async (
            MetricsQueryService query,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetEventsAsync<ScheduleExecutionEvent>(
                "metrics:schedules:", fromDate, toDate);
        });

        api.MapGet("/health", async (MetricsQueryService query) =>
        {
            return await query.GetHealthAsync();
        });

        api.MapGet("/tokens/by-user", async (
            MetricsQueryService query,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetTokenBreakdownAsync("tokens:byUser:", fromDate, toDate);
        });

        api.MapGet("/tokens/by-model", async (
            MetricsQueryService query,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetTokenBreakdownAsync("tokens:byModel:", fromDate, toDate);
        });
    }
}
