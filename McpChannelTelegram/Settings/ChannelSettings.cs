namespace McpChannelTelegram.Settings;

public record ChannelSettings
{
    public required AgentBotConfig[] Bots { get; init; }
    public required string[] AllowedUsernames { get; init; }
}

public record AgentBotConfig
{
    public required string AgentId { get; init; }
    public required string BotToken { get; init; }
}
