using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record AgentConfigPatch
{
    // Every value McpAgent.ParseEffort accepts; the WebChat effort dropdown offers exactly this list.
    public static readonly IReadOnlyList<string> SupportedEfforts =
        ["none", "low", "medium", "high", "xhigh", "max"];

    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
}