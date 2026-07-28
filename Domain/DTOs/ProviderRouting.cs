using JetBrains.Annotations;

namespace Domain.DTOs;

// OpenRouter provider-routing preferences, serialized into the request body's `provider`
// object. Every property is nullable so that an unset field is omitted from the wire object
// rather than sent as JSON null -- OpenRouter's balanced load balancing is only available by
// omitting `sort` and `order` entirely, so "absent" has to stay expressible.
[PublicAPI]
public record ProviderRouting
{
    public ProviderSort? Sort { get; init; }
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