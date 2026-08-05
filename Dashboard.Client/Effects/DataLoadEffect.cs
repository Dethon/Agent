using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;

namespace Dashboard.Client.Effects;

public sealed class DataLoadEffect(
    MetricsApiService api,
    MetricFamilyTable families,
    MetricsStore metricsStore,
    HealthStore healthStore)
{
    // The one trace the swallowed failure leaves behind. The live connection skips catch-up on its
    // first epoch on the premise that this load delivered the same data; a recorded failure is how
    // it knows that premise did not hold.
    public bool LastLoadFailed { get; private set; }

    public async Task LoadAsync(DateOnly from, DateOnly to)
    {
        try
        {
            families.All.ToList().ForEach(family => family.SetDateRange(from, to));

            var summaryTask = api.GetSummaryAsync(from, to);
            var healthTask = api.GetHealthAsync();
            var familyTasks = families.All
                .SelectMany(family => new[] { family.LoadEventsAsync(), family.RefreshAsync() });

            await Task.WhenAll([summaryTask, healthTask, .. familyTasks]);

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

            var health = await healthTask;
            if (health is not null)
            {
                healthStore.UpdateHealth(health
                    .Select(h => new ServiceHealth(h.Service, h.IsHealthy, h.LastSeen))
                    .ToList());
            }

            LastLoadFailed = false;
        }
        catch
        {
            // The page-load path swallows the reason a load failed, as it always has. Connection
            // status is the live connection's to publish, and a failed request is not an outage.
            LastLoadFailed = true;
        }
    }
}