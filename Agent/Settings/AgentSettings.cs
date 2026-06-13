using Domain.DTOs;
using JetBrains.Annotations;

namespace Agent.Settings;

public record AgentSettings
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required RedisConfiguration Redis { get; init; }
    public required AgentDefinition[] Agents { get; [UsedImplicitly] init; }
    public ChannelEndpoint[] ChannelEndpoints { get; init; } = [];
    public SubAgentDefinition[] SubAgents { get; init; } = [];
}

public record OpenRouterConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string ApiKey { get; [UsedImplicitly] init; }
    public int? MaxContextTokens { get; [UsedImplicitly] init; }
}

public record RedisConfiguration
{
    public required string ConnectionString { get; [UsedImplicitly] init; }
    public int? ExpirationDays { get; [UsedImplicitly] init; }
}

public record ChannelEndpoint
{
    public required string ChannelId { get; init; }
    public required string Endpoint { get; init; }

    // Attach-only channels (e.g. voice) cannot own a conversation; delivery fan-out
    // orders them last so a topic-owning channel anchors the shared conversation id.
    public bool AttachOnly { get; init; }
}