using Domain.DTOs;

namespace Domain.Contracts;

public interface IMemoryConsolidator
{
    Task<IReadOnlyList<MergeDecision>> ConsolidateAsync(
        IReadOnlyList<MemoryEntry> memories, CancellationToken ct);

    Task<PersonalityProfile> SynthesizeProfileAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct);
}

public record MergeDecision(
    IReadOnlyList<string> SourceIds,
    MergeAction Action,
    string? MergedContent = null,
    MemoryCategory? Category = null,
    double? Importance = null,
    IReadOnlyList<string>? Tags = null);

public enum MergeAction
{
    Keep,
    Merge,
    SupersedeOlder
}