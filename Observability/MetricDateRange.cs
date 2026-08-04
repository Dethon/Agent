using System.Globalization;

namespace Observability;

public sealed record MetricDateRange(DateOnly From, DateOnly To)
{
    public static ValueTask<MetricDateRange?> BindAsync(HttpContext context)
    {
        var time = context.RequestServices.GetRequiredService<TimeProvider>();
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        return ValueTask.FromResult(
            Read(context, "from", today) is { } from && Read(context, "to", today) is { } to
                ? new MetricDateRange(from, to)
                : null);
    }

    // A value that is present but will not parse yields null, which the framework answers with the
    // same 400 the two nullable DateOnly parameters gave. Only an absent value takes the default.
    private static DateOnly? Read(HttpContext context, string key, DateOnly fallback) =>
        !context.Request.Query.TryGetValue(key, out var raw)
            ? fallback
            : DateOnly.TryParse(raw.ToString(), CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
}