namespace Cli.Settings;

public record AgentConfiguration
{
    public required string OpenRouterApiUrl { get; init; }
    public required string OpenRouterApiKey { get; init; }
    public required string OpenRouterModel { get; init; }
};