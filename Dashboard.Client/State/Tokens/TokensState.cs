using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tokens;

public record TokensState
{
    public IReadOnlyList<TokenUsageEvent> Events { get; init; } = [];
    public TokenDimension GroupBy { get; init; } = TokenDimension.User;
    public TokenMetric Metric { get; init; } = TokenMetric.Tokens;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
}
