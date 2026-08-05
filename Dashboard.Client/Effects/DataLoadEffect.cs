using Dashboard.Client.Metrics;

namespace Dashboard.Client.Effects;

public sealed class DataLoadEffect(MetricFamilyTable families, OverviewFigures overview)
{
    // The one trace the swallowed failure leaves behind. The live connection skips catch-up on its
    // first epoch on the premise that this load delivered the same data; a recorded failure is how
    // it knows that premise did not hold.
    public bool LastLoadFailed { get; private set; }

    // Raised after every load has settled, success or failure. The hub can become live while the
    // initial load is still in flight, so the flag alone is not enough: whoever skipped catch-up on
    // its promise needs to hear when the load actually delivers or fails.
    public event Func<Task>? LoadCompleted;

    public async Task LoadAsync(DateOnly from, DateOnly to)
    {
        families.All.ToList().ForEach(family => family.SetDateRange(from, to));
        overview.SetDateRange(from, to);

        var requests = new Func<Task>[]
        {
            overview.LoadSummaryAsync,
            overview.LoadHealthAsync,
        }.Concat(families.All.SelectMany(family => new Func<Task>[]
        {
            family.LoadEventsAsync,
            family.RefreshAsync,
        }));

        var outcomes = await Task.WhenAll(requests.Select(SettleAsync));

        LastLoadFailed = outcomes.Contains(false);

        if (LoadCompleted is { } completed)
        {
            await completed();
        }
    }

    // Each request settles on its own, because one endpoint that fails must not take the panels that
    // did answer off the screen: the KPI row and the health grid used to go blank whenever any of
    // the nineteen breakdown requests failed. A refresh can also hand back a run started by a live
    // push and already failing, which is a failure this load never issued and still has to survive.
    // The page-load path swallows the reason, as it always has — connection status is the live
    // connection's to publish, and a failed request is not an outage.
    private static async Task<bool> SettleAsync(Func<Task> request)
    {
        try
        {
            await request();
            return true;
        }
        catch
        {
            return false;
        }
    }
}