namespace McpChannelSignalR.Settings;

public record ChannelSettings
{
    public required string RedisConnectionString { get; init; }
}
