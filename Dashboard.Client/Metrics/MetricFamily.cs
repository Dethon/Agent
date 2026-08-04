namespace Dashboard.Client.Metrics;

public class MetricFamily(
    string name,
    MetricChoice groupBy,
    MetricChoice? metric,
    Action<DateOnly, DateOnly> setDateRange,
    Func<Task> loadEvents,
    Func<Task> refreshBreakdown)
{
    private readonly object _gate = new();
    private Task? _running;
    private bool _dirty;

    public string Name { get; } = name;

    public string PreferenceKeyPrefix { get; } = $"{name}.";

    public MetricChoice GroupBy { get; } = groupBy;

    // Null for a family with nothing to choose between: errors and schedules have no metric pill,
    // because there is no quantity to pick.
    public MetricChoice? Metric { get; } = metric;

    public void SetDateRange(DateOnly from, DateOnly to) => setDateRange(from, to);

    public Task LoadEventsAsync() => loadEvents();

    // Awaiting this means the breakdown reflects the store state at or after the call. A caller
    // arriving while a run is in flight shares that run and marks it stale, so the run repeats with
    // the newer state and a burst of events costs two requests instead of one per event. There is
    // no timer and no waiting window, so nothing is slower than a single refresh. Failure throws to
    // everyone awaiting, because the two callers have different policies for it.
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_running is { } running)
            {
                _dirty = true;
                return running;
            }

            var run = RunAsync();
            _running = run.IsCompleted ? null : run;
            return run;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await refreshBreakdown();

                lock (_gate)
                {
                    if (!_dirty)
                    {
                        return;
                    }

                    _dirty = false;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _dirty = false;
            }
        }
    }
}

public sealed class MetricFamily<TStore>(
    TStore store,
    string name,
    MetricChoice groupBy,
    MetricChoice? metric,
    Action<DateOnly, DateOnly> setDateRange,
    Func<Task> loadEvents,
    Func<Task> refreshBreakdown)
    : MetricFamily(name, groupBy, metric, setDateRange, loadEvents, refreshBreakdown)
    where TStore : class
{
    public TStore Store { get; } = store;
}