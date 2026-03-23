using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
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

        api.MapGet("/errors/range", async (
            MetricsQueryService query, DateOnly? from, DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetEventsAsync<ErrorEvent>("metrics:errors:", fromDate, toDate);
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

        api.MapGet("/tokens/by/{dimension}", async (
            MetricsQueryService query,
            TokenDimension dimension,
            TokenMetric metric,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetTokenGroupedAsync(dimension, metric, fromDate, toDate);
        });

        api.MapGet("/tools/by/{dimension}", async (
            MetricsQueryService query,
            ToolDimension dimension,
            ToolMetric metric,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetToolGroupedAsync(dimension, metric, fromDate, toDate);
        });

        api.MapGet("/errors/by/{dimension}", async (
            MetricsQueryService query,
            ErrorDimension dimension,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetErrorGroupedAsync(dimension, fromDate, toDate);
        });

        api.MapGet("/schedules/by/{dimension}", async (
            MetricsQueryService query,
            ScheduleDimension dimension,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetScheduleGroupedAsync(dimension, fromDate, toDate);
        });
    }
}
