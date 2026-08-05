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
    private (DateOnly From, DateOnly To)? _range;

    public void SetDateRange(DateOnly from, DateOnly to) => _range = (from, to);

    // The summary is per range and catch-up is never told one, so it re-reads over the range the
    // last page load set, exactly as the family walk reads its range from the families. Before any
    // load has set one there is no summary on screen to correct, so there is nothing to read.
    public async Task LoadSummaryAsync()
    {
        if (_range is not { } range)
        {
            return;
        }

        var summary = await api.GetSummaryAsync(range.From, range.To);
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