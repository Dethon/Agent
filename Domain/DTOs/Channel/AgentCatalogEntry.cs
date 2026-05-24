using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record AgentCatalogEntry(string Id, string Name, string? Description);