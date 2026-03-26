using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
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
    ConnectionStore connectionStore)
{
    public async Task LoadAsync(DateOnly from, DateOnly to)
    {
        try
        {
            tokensStore.SetDateRange(from, to);
            toolsStore.SetDateRange(from, to);
            errorsStore.SetDateRange(from, to);
            schedulesStore.SetDateRange(from, to);

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

            await Task.WhenAll(summaryTask, tokensTask, toolsTask, errorsTask,
                schedulesTask, healthTask, tokenBreakdownTask, toolBreakdownTask,
                errorBreakdownTask, scheduleBreakdownTask);

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