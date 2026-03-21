namespace McpChannelTelegram.Settings;

public record ChannelSettings
{
    public required string RedisConnectionString { get; init; }
    public required string BotToken { get; init; }
    public required string[] AllowedUsernames { get; init; }
}
