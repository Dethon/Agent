using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Tokens;

public record TokensState
{
    public IReadOnlyList<TokenUsageEvent> Events { get; init; } = [];
    public Dictionary<string, long> ByUser { get; init; } = [];
    public Dictionary<string, long> ByModel { get; init; } = [];
}
