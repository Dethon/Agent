using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
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

    // OpenRouter ranks by exactly one `sort`; a second criterion is expressed as a threshold
    // sitting under it. Thresholds deprioritize rather than filter -- an endpoint that misses
    // one drops to the end of the candidate list instead of being removed -- so combining them
    // with a sort can never leave a request with nowhere to route. `MaxPrice` is the exception:
    // it is a real ceiling and does exclude.
    public ProviderThreshold? PreferredMinThroughput { get; init; }
    public ProviderThreshold? PreferredMaxLatency { get; init; }
    public ProviderMaxPrice? MaxPrice { get; init; }

    public bool IsEmpty =>
        Sort is null &&
        Order is not { Length: > 0 } &&
        Only is not { Length: > 0 } &&
        Ignore is not { Length: > 0 } &&
        AllowFallbacks is null &&
        PreferredMinThroughput is not { IsEmpty: false } &&
        PreferredMaxLatency is not { IsEmpty: false } &&
        MaxPrice is not { IsEmpty: false };
}

// A throughput floor (tokens/second) or a latency ceiling (seconds), measured per percentile.
// OpenRouter accepts a bare number as shorthand for the p50 cutoff, which is the spelling its
// own examples use; the TypeConverter exists so configuration can use it too, since the binder
// otherwise refuses a scalar value on a complex-typed key.
[PublicAPI]
[TypeConverter(typeof(ProviderThresholdConverter))]
public record ProviderThreshold
{
    private readonly double? _p50;
    private readonly double? _p75;
    private readonly double? _p90;
    private readonly double? _p99;

    public double? P50 { get => _p50; init => _p50 = ProviderCutoff.Validated(value, nameof(P50)); }
    public double? P75 { get => _p75; init => _p75 = ProviderCutoff.Validated(value, nameof(P75)); }
    public double? P90 { get => _p90; init => _p90 = ProviderCutoff.Validated(value, nameof(P90)); }
    public double? P99 { get => _p99; init => _p99 = ProviderCutoff.Validated(value, nameof(P99)); }

    public bool IsEmpty => P50 is null && P75 is null && P90 is null && P99 is null;
}

// Per-token ceilings in dollars per million tokens (`Prompt`, `Completion`) and per unit
// (`Request`, `Image`). Unlike the preferred-* thresholds this one excludes providers outright.
[PublicAPI]
public record ProviderMaxPrice
{
    private readonly double? _prompt;
    private readonly double? _completion;
    private readonly double? _request;
    private readonly double? _image;

    public double? Prompt
    {
        get => _prompt;
        init => _prompt = ProviderCutoff.Validated(value, nameof(Prompt));
    }

    public double? Completion
    {
        get => _completion;
        init => _completion = ProviderCutoff.Validated(value, nameof(Completion));
    }

    public double? Request
    {
        get => _request;
        init => _request = ProviderCutoff.Validated(value, nameof(Request));
    }

    public double? Image
    {
        get => _image;
        init => _image = ProviderCutoff.Validated(value, nameof(Image));
    }

    public bool IsEmpty => Prompt is null && Completion is null && Request is null && Image is null;
}

// A negative or non-finite cutoff is silent in a way a wrong sort is not: it deprioritizes
// nothing and excludes nobody, so the only symptom is routing that ignores the preference.
internal static class ProviderCutoff
{
    public static double? Validated(double? value, string name)
    {
        return value is { } cutoff && (!double.IsFinite(cutoff) || cutoff < 0)
            ? throw new ArgumentOutOfRangeException(
                name, value, $"'{name}' must be a finite, non-negative number.")
            : value;
    }
}

// The binder hands every configuration value over as a string, so the p50 shorthand can only be
// accepted here.
[PublicAPI]
public sealed class ProviderThresholdConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        return value is string text
            ? new ProviderThreshold { P50 = ParseCutoff(text) }
            : base.ConvertFrom(context, culture, value);
    }

    private static double ParseCutoff(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cutoff)
            ? cutoff
            : throw new FormatException($"'{text}' is not a number.");
    }
}

// An enum rather than a string so configuration binding rejects a typo at bind time, naming
// the offending path, instead of sending a value OpenRouter would silently ignore. The
// converter is for the other entry point: /api/agents deserializes a registration with
// JsonSerializerDefaults.Web, which reads an enum as a number, and a string is the only
// spelling an external host's JSON can carry.
[JsonConverter(typeof(JsonStringEnumConverter<ProviderSort>))]
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