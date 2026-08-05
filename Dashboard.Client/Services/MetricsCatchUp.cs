using Dashboard.Client.Contracts;
using Dashboard.Client.Metrics;

namespace Dashboard.Client.Services;

// Everything a page load reads: a walk of the family table, plus the summary totals and the health
// roster that belong to no family. It is the same set for a reason — whatever a page load can put on
// screen is what an outage can leave stale, and the KPI row was the half nobody re-read. The range
// comes from what the families and the last load already hold, which is what leaves the user's
// group-by, metric and time choices where they were, so recovering does not move the page under them.
public sealed class MetricsCatchUp(MetricFamilyTable families, OverviewFigures overview) : IMetricsCatchUp
{
    public Task CatchUpAsync() =>
        Task.WhenAll(new[] { overview.LoadSummaryAsync(), overview.LoadHealthAsync() }
            .Concat(families.All
                .SelectMany(family => new[] { family.LoadEventsAsync(), family.RefreshAsync() })));
}