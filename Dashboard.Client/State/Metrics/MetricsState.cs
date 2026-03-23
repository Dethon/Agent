namespace Dashboard.Client.State.Metrics;

public record MetricsState
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal Cost { get; init; }
    public long ToolCalls { get; init; }
    public long ToolErrors { get; init; }
}
