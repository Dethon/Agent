namespace Domain.DTOs.Metrics;

public record TokenUsageEvent : MetricEvent
{
    public required string Sender { get; init; }
    public required string Model { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required decimal Cost { get; init; }

    // Prompt-cache hits, as reported by the provider. Null means the provider said nothing about
    // caching — deliberately not 0, so "no detail" stays distinguishable from "nothing was cached".
    // Without this the hit rate can only be inferred from cost against list pricing, which the
    // :nitro routing variants make unreliable.
    public long? CachedInputTokens { get; init; }
}