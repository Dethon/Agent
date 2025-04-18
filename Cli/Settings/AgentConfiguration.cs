using JetBrains.Annotations;

namespace Cli.Settings;

public record AgentConfiguration
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required JackettConfiguration Jackett { get; init; }
}

[UsedImplicitly]
public record OpenRouterConfiguration
{
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
}

[UsedImplicitly]
public record JackettConfiguration
{
    public required string ApiKey { get; init; }
    public required string ApiUrl { get; init; }
}