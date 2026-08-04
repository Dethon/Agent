using System.Globalization;
using Dashboard.Client.Effects;
using Dashboard.Client.Services;

namespace Dashboard.Client.Metrics;

// What every breakdown page used to write out for itself: read the saved choices, derive the date
// range from the selected day count, persist a pill that moves and reload. It holds no markup, so a
// change to any of it is reachable without a browser.
public sealed class MetricControlsSession(
    MetricFamily family,
    LocalStorageService storage,
    TimeProvider time,
    DataLoadEffect dataLoad)
{
    public int SelectedDays { get; private set; } = 1;

    public DateOnly From { get; private set; }

    public DateOnly To { get; private set; }

    public async Task InitializeAsync()
    {
        await ApplySavedAsync(family.GroupBy);

        if (family.Metric is { } metric)
        {
            await ApplySavedAsync(metric);
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
        await storage.SetAsync(KeyFor(choice.Key), value);
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