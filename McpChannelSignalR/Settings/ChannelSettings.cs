namespace McpChannelSignalR.Settings;

public record ChannelSettings
{
    public required string RedisConnectionString { get; init; }
    public AgentConfig[] Agents { get; init; } = [];
}

public record AgentConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
