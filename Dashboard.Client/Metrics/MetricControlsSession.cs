using System.Globalization;
using Dashboard.Client.Effects;
using Dashboard.Client.Services;

namespace Dashboard.Client.Metrics;

// What every breakdown page used to write out for itself: read the saved choices, derive the date
// range from the selected day count, persist a pill that moves and reload. It holds no markup, so a
// change to any of it is reachable without a browser.
public sealed class MetricControlsSession(
    MetricFamily family,
    IReadOnlyList<MetricChoice> extraChoices,
    LocalStorageService storage,
    TimeProvider time,
    DataLoadEffect dataLoad)
{
    public int SelectedDays { get; private set; } = 1;

    public DateOnly From { get; private set; }

    public DateOnly To { get; private set; }

    // Every choice this session can save is a choice it restores, extras included. A choice that is
    // written and never read back is how the voice aggregation quietly reset to Avg on every visit.
    private IEnumerable<MetricChoice> Choices =>
        new[] { family.Dimension, family.Metric }.OfType<MetricChoice>().Concat(extraChoices);

    public async Task InitializeAsync()
    {
        foreach (var choice in Choices)
        {
            await ApplySavedAsync(choice);
        }

        var savedDays = await storage.GetIntAsync(KeyFor("days"));
        if (savedDays is > 0)
        {
            SelectedDays = savedDays.Value;
        }

        DeriveRange();
        await dataLoad.LoadAsync(From, To);
    }

    public async Task ChangeAsync(MetricChoice choice, string value)
    {
        choice.Apply(value);

        // Every choice, not just the one that moved: applying a group-by can swap the metric when
        // the combination is disallowed, and persisting only the moved pill would restore that
        // disallowed combination on the next visit.
        foreach (var each in Choices)
        {
            await storage.SetAsync(KeyFor(each.Key), each.Current);
        }

        await family.RefreshAsync();
    }

    public async Task ChangeDaysAsync(string value)
    {
        SelectedDays = int.Parse(value, CultureInfo.InvariantCulture);
        DeriveRange();
        await storage.SetAsync(KeyFor("days"), value);
        await dataLoad.LoadAsync(From, To);
    }

    private async Task ApplySavedAsync(MetricChoice choice)
    {
        var saved = await storage.GetAsync(KeyFor(choice.Key));
        if (!string.IsNullOrEmpty(saved))
        {
            choice.Apply(saved);
        }
    }

    private string KeyFor(string key) => $"{family.PreferenceKeyPrefix}{key}";

    private void DeriveRange()
    {
        To = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        From = To.AddDays(-(SelectedDays - 1));
    }
}