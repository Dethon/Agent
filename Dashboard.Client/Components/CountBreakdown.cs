namespace Dashboard.Client.Components;

// Errors and schedules count things, so their breakdowns are integers, and the chart draws
// decimals. The two families that need this conversion get one copy of it.
public static class CountBreakdown
{
    public static Dictionary<string, decimal> AsChartData(this Dictionary<string, int> counts) =>
        counts.ToDictionary(entry => entry.Key, entry => (decimal)entry.Value);
}