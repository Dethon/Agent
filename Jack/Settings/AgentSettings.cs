using JetBrains.Annotations;

namespace Jack.Settings;

public record AgentSettings
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required TelegramConfiguration Telegram { get; init; }
    public required Mcp[] McpServers { get; init; }
    public required RedisConfiguration Redis { get; init; }
}

public record OpenRouterConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string ApiKey { get; [UsedImplicitly] init; }
    public required string[] Models { get; [UsedImplicitly] init; }
}

public record TelegramConfiguration
{
    public required string BotToken { get; [UsedImplicitly] init; }
    public required string[] AllowedUserNames { get; [UsedImplicitly] init; }
}

public record Mcp
{
    public required string Endpoint { get; [UsedImplicitly] init; }
}

public record RedisConfiguration
{
    public required string ConnectionString { get; [UsedImplicitly] init; }
    public int ConversationExpiryDays { get; [UsedImplicitly] init; }
}