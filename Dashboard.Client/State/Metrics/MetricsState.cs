namespace Dashboard.Client.State.Metrics;

public record MetricsState
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal Cost { get; init; }
    public long ToolCalls { get; init; }
    public long ToolErrors { get; init; }
    public long TotalRecalls { get; init; }
    public long TotalExtractions { get; init; }
    public long TotalDreamings { get; init; }
    public long MemoriesStored { get; init; }
    public long MemoriesMerged { get; init; }
    public long MemoriesDecayed { get; init; }
}