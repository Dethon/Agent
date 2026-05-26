using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record RegisterAgentsParams
{
    public required IReadOnlyList<AgentCatalogEntry> Agents { get; init; }
}