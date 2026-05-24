namespace McpServerScheduling.Settings;

public record SchedulingSettings
{
    public required string RedisConnectionString { get; init; }
    public int DispatchIntervalSeconds { get; init; } = 30;
    public IReadOnlyList<string> DefaultDeliverTo { get; init; } = [];
    public IReadOnlyList<SchedulingAgentConfig> Agents { get; init; } = [];
}

public record SchedulingAgentConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}