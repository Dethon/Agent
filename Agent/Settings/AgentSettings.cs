using Domain.DTOs;
using JetBrains.Annotations;

namespace Agent.Settings;

public record AgentSettings
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required TelegramConfiguration Telegram { get; init; }
    public required RedisConfiguration Redis { get; init; }
    public ServiceBusSettings? ServiceBus { get; [UsedImplicitly] init; }
    public required AgentDefinition[] Agents { get; [UsedImplicitly] init; }
}

public record OpenRouterConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string ApiKey { get; [UsedImplicitly] init; }
}

public record TelegramConfiguration
{
    public required string[] AllowedUserNames { get; [UsedImplicitly] init; }
}

public record RedisConfiguration
{
    public required string ConnectionString { get; [UsedImplicitly] init; }
    public int? ExpirationDays { get; [UsedImplicitly] init; }
}