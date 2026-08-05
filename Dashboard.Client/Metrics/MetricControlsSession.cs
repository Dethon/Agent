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
        await RestoreChoicesAsync();

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

        try
        {
            await family.RefreshAsync();
        }
        catch
        {
            // The pill has moved and is saved; a refresh that fails leaves the breakdown at its
            // last known value, exactly as the page-load path settles the same failure. Letting it
            // out of a UI event handler would put the whole page into Blazor's unhandled-error UI.
        }
    }

    public async Task ChangeDaysAsync(string value)
    {
        SelectedDays = int.Parse(value, CultureInfo.InvariantCulture);
        DeriveRange();
        await storage.SetAsync(KeyFor("days"), value);
        await dataLoad.LoadAsync(From, To);
    }

    // The dimension is restored last because applying it is what coerces a disallowed metric.
    // Restored first, the saved metric would land on top of that coercion and resurrect a stale
    // combination an older build persisted, selecting a disabled pill. Whatever the coercion moved
    // is saved back, so the stale pair cannot return on the next visit either.
    private async Task RestoreChoicesAsync()
    {
        var saved = new Dictionary<string, string?>();
        foreach (var choice in Choices.Where(c => c != family.Dimension).Append(family.Dimension))
        {
            saved[choice.Key] = await ApplySavedAsync(choice);
        }

        foreach (var coerced in Choices.Where(c => !string.IsNullOrEmpty(saved[c.Key]) && saved[c.Key] != c.Current))
        {
            await storage.SetAsync(KeyFor(coerced.Key), coerced.Current);
        }
    }

    private async Task<string?> ApplySavedAsync(MetricChoice choice)
    {
        var saved = await storage.GetAsync(KeyFor(choice.Key));
        if (!string.IsNullOrEmpty(saved))
        {
            choice.Apply(saved);
        }

        return saved;
    }

    private string KeyFor(string key) => $"{family.PreferenceKeyPrefix}{key}";

    private void DeriveRange()
    {
        To = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        From = To.AddDays(-(SelectedDays - 1));
    }
}