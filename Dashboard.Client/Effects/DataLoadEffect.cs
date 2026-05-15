using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;

namespace Dashboard.Client.Effects;

public sealed class DataLoadEffect(
    MetricsApiService api,
    MetricsStore metricsStore,
    HealthStore healthStore,
    TokensStore tokensStore,
    ToolsStore toolsStore,
    ErrorsStore errorsStore,
    SchedulesStore schedulesStore,
    ConnectionStore connectionStore,
    MemoryStore memoryStore,
    LatencyStore latencyStore)
{
    public async Task LoadAsync(DateOnly from, DateOnly to)
    {
        try
        {
            tokensStore.SetDateRange(from, to);
            toolsStore.SetDateRange(from, to);
            errorsStore.SetDateRange(from, to);
            schedulesStore.SetDateRange(from, to);
            memoryStore.SetDateRange(from, to);
            latencyStore.SetDateRange(from, to);

            var summaryTask = api.GetSummaryAsync(from, to);
            var tokensTask = api.GetTokensAsync(from, to);
            var toolsTask = api.GetToolsAsync(from, to);
            var errorsTask = api.GetErrorsAsync(from, to);
            var schedulesTask = api.GetSchedulesAsync(from, to);
            var healthTask = api.GetHealthAsync();

            var tokenBreakdownTask = api.GetTokenGroupedAsync(
                tokensStore.State.GroupBy, tokensStore.State.Metric, from, to);
            var toolBreakdownTask = api.GetToolGroupedAsync(
                toolsStore.State.GroupBy, toolsStore.State.Metric, from, to);
            var errorBreakdownTask = api.GetErrorGroupedAsync(
                errorsStore.State.GroupBy, from, to);
            var scheduleBreakdownTask = api.GetScheduleGroupedAsync(
                schedulesStore.State.GroupBy, from, to);
            var memoryRecallTask = api.GetMemoryRecallAsync(from, to);
            var memoryExtractionTask = api.GetMemoryExtractionAsync(from, to);
            var memoryDreamingTask = api.GetMemoryDreamingAsync(from, to);
            var memoryBreakdownTask = api.GetMemoryGroupedAsync(
                memoryStore.State.GroupBy, memoryStore.State.Metric, from, to);

            var latencyTask = api.GetLatencyAsync(from, to);
            var latencyBreakdownTask = api.GetLatencyGroupedAsync(
                latencyStore.State.GroupBy, latencyStore.State.Metric, from, to);
            var latencyTrendTask = api.GetLatencyTrendAsync(latencyStore.State.Metric, from, to);

            await Task.WhenAll(summaryTask, tokensTask, toolsTask, errorsTask,
                schedulesTask, healthTask, tokenBreakdownTask, toolBreakdownTask,
                errorBreakdownTask, scheduleBreakdownTask,
                memoryRecallTask, memoryExtractionTask, memoryDreamingTask, memoryBreakdownTask,
                latencyTask, latencyBreakdownTask, latencyTrendTask);

            var summary = await summaryTask;
            if (summary is not null)
            {
                metricsStore.UpdateSummary(new MetricsState
                {
                    InputTokens = summary.InputTokens,
                    OutputTokens = summary.OutputTokens,
                    Cost = summary.Cost,
                    ToolCalls = summary.ToolCalls,
                    ToolErrors = summary.ToolErrors,
                    TotalRecalls = summary.TotalRecalls,
                    TotalExtractions = summary.TotalExtractions,
                    TotalDreamings = summary.TotalDreamings,
                    MemoriesStored = summary.MemoriesStored,
                    MemoriesMerged = summary.MemoriesMerged,
                    MemoriesDecayed = summary.MemoriesDecayed,
                });
            }

            tokensStore.SetEvents(await tokensTask ?? []);
            toolsStore.SetEvents(await toolsTask ?? []);
            errorsStore.SetEvents(await errorsTask ?? []);
            schedulesStore.SetEvents(await schedulesTask ?? []);

            tokensStore.SetBreakdown(await tokenBreakdownTask ?? []);
            toolsStore.SetBreakdown(await toolBreakdownTask ?? []);
            errorsStore.SetBreakdown(await errorBreakdownTask ?? []);
            schedulesStore.SetBreakdown(await scheduleBreakdownTask ?? []);

            memoryStore.SetRecallEvents(await memoryRecallTask ?? []);
            memoryStore.SetExtractionEvents(await memoryExtractionTask ?? []);
            memoryStore.SetDreamingEvents(await memoryDreamingTask ?? []);
            memoryStore.SetBreakdown(await memoryBreakdownTask ?? []);

            latencyStore.SetEvents(await latencyTask ?? []);
            latencyStore.SetBreakdown(await latencyBreakdownTask ?? []);
            latencyStore.SetTrend(await latencyTrendTask ?? []);

            var health = await healthTask;
            if (health is not null)
            {
                healthStore.UpdateHealth(health
                    .Select(h => new ServiceHealth(h.Service, h.IsHealthy, h.LastSeen))
                    .ToList());
            }

            connectionStore.SetConnected(true);
        }
        catch
        {
            connectionStore.SetConnected(false);
        }
    }

}