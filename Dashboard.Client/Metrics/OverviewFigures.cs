using Dashboard.Client.Services;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;

namespace Dashboard.Client.Metrics;

// The two reads that belong to no metric family: the summary totals behind the Overview KPI row and
// the service health roster. They sit beside the family table because the page load and the catch-up
// walk both want them, and neither should own the mapping from response to store.
public sealed class OverviewFigures(
    MetricsApiService api,
    MetricsStore metricsStore,
    HealthStore healthStore)
{
    public async Task LoadSummaryAsync(DateOnly from, DateOnly to)
    {
        var summary = await api.GetSummaryAsync(from, to);
        if (summary is null)
        {
            return;
        }

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

    public async Task LoadHealthAsync()
    {
        var health = await api.GetHealthAsync();
        if (health is null)
        {
            return;
        }

        healthStore.UpdateHealth(health
            .Select(h => new ServiceHealth(h.Service, h.IsHealthy, h.LastSeen))
            .ToList());
    }
}