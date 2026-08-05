using Domain.DTOs.Metrics;

namespace Dashboard.Client.State;

// The one bounded append every store's event list is written through. An append used to be a copy
// with one more event on the end and nothing that ever took one off, so a tab left open collected
// every push since the last full load — and the Overview feed re-sorts all of them on each one.
//
// Only an append trims. A load writes whatever the range answered with, however long, because that
// is what the user asked to see; an append then keeps the list at the length the load left it, or
// grows it to the cap, whichever is longer. So a push can cost the oldest event on screen, and never
// the rest of the month.
public static class EventWindow
{
    public const int Cap = 2000;

    public static IReadOnlyList<T> Append<T>(IReadOnlyList<T> events, T evt) where T : MetricEvent
    {
        ArgumentNullException.ThrowIfNull(events);

        var limit = Math.Max(Cap, events.Count);
        return events.Count < limit
            ? [.. events, evt]
            : [.. events.Skip(events.Count - limit + 1), evt];
    }
}