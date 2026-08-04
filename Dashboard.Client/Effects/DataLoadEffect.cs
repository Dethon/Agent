using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;

namespace Dashboard.Client.Effects;

public sealed class DataLoadEffect(
    MetricsApiService api,
    MetricFamilyTable families,
    MetricsStore metricsStore,
    HealthStore healthStore,
    ConnectionStore connectionStore)
{
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

            connectionStore.SetConnected(true);
        }
        catch
        {
            connectionStore.SetConnected(false);
        }
    }
}