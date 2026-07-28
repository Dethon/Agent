using JetBrains.Annotations;

namespace Domain.DTOs;

// OpenRouter provider-routing preferences, serialized into the request body's `provider`
// object. Every property is nullable so that an unset field is omitted from the wire object
// rather than sent as JSON null -- OpenRouter's balanced load balancing is only available by
// omitting `sort` and `order` entirely, so "absent" has to stay expressible.
[PublicAPI]
public record ProviderRouting
{
    private readonly ProviderSort? _sort;

    // Enum.Parse accepts numeric strings including undefined values, so bind-time enum
    // conversion alone lets "sort": 7 through as (ProviderSort)7 and onto the wire as "7".
    // Guarding the property covers binding and direct construction with one check.
    public ProviderSort? Sort
    {
        get => _sort;
        init => _sort = value is { } sort && !Enum.IsDefined(sort)
            ? throw new ArgumentOutOfRangeException(
                nameof(Sort), value, $"'{value}' is not a defined {nameof(ProviderSort)}.")
            : value;
    }
    public string[]? Order { get; init; }
    public string[]? Only { get; init; }
    public string[]? Ignore { get; init; }
    public bool? AllowFallbacks { get; init; }

    public bool IsEmpty =>
        Sort is null &&
        Order is not { Length: > 0 } &&
        Only is not { Length: > 0 } &&
        Ignore is not { Length: > 0 } &&
        AllowFallbacks is null;
}

// An enum rather than a string so configuration binding rejects a typo at bind time, naming
// the offending path, instead of sending a value OpenRouter would silently ignore.
public enum ProviderSort
{
    Price,
    Throughput,
    Latency
}

// Two routing configurations are legal, silent, and almost certainly not what the author meant.
// Neither is visible in a response, so they are reported at agent construction instead.
public static class ProviderRoutingAdvisories
{
    private static readonly (string Suffix, ProviderSort Sort)[] _suffixSorts =
    [
        (":nitro", ProviderSort.Throughput),
        (":floor", ProviderSort.Price)
    ];

    public static IReadOnlyList<string> For(string model, ProviderRouting? routing)
    {
        return routing is null
            ? []
            : new[] { SuffixConflict(model, routing), StickyRoutingLoss(routing) }
                .OfType<string>()
                .ToList();
    }

    private static string? SuffixConflict(string model, ProviderRouting routing)
    {
        if (routing.Sort is not { } sort)
        {
            return null;
        }

        var match = _suffixSorts.FirstOrDefault(
            s => model.EndsWith(s.Suffix, StringComparison.OrdinalIgnoreCase));

        return match.Suffix is not null && match.Sort != sort
            ? $"model '{model}' carries the '{match.Suffix}' suffix, which means sort "
              + $"'{Name(match.Sort)}', but providerRouting.sort is '{Name(sort)}'. OpenRouter "
              + "does not document which wins -- remove one."
            : null;
    }

    private static string? StickyRoutingLoss(ProviderRouting routing)
    {
        return routing.Order is { Length: > 0 }
            ? "providerRouting.order disables OpenRouter sticky routing, so the session_id on "
              + "each request is ignored and the prompt cache goes cold every turn. Use 'only' "
              + "with 'sort' to restrict the provider set without that cost."
            : null;
    }

    private static string Name(ProviderSort sort) => sort.ToString().ToLowerInvariant();
}