using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record AgentCatalogEntry(
    string Id,
    string Name,
    string? Description,
    string? DefaultModel = null,
    string? DefaultReasoningEffort = null,
    IReadOnlyList<PatchableModel>? PatchableModels = null,
    IReadOnlyList<string>? PatchableReasoningEfforts = null);