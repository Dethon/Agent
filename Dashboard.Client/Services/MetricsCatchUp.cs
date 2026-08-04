using Dashboard.Client.Contracts;
using Dashboard.Client.Metrics;

namespace Dashboard.Client.Services;

// A walk of the family table: the same events and breakdown a page load fetches, over the range
// each family already holds. Reading the range from the families is what leaves the user's group-by,
// metric and time choices where they were, so recovering does not move the page under them.
public sealed class MetricsCatchUp(MetricFamilyTable families) : IMetricsCatchUp
{
    public Task CatchUpAsync() =>
        Task.WhenAll(families.All
            .SelectMany(family => new[] { family.LoadEventsAsync(), family.RefreshAsync() }));
}