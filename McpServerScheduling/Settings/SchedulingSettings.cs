namespace McpServerScheduling.Settings;

public record SchedulingSettings
{
    public required string RedisConnectionString { get; init; }
    public int DispatchIntervalSeconds { get; init; } = 30;
    public IReadOnlyList<string> DefaultDeliverTo { get; init; } = [];
}