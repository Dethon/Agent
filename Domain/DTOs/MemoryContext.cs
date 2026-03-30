using Domain.Contracts;

namespace Domain.DTOs;

public record MemoryContext(
    IReadOnlyList<MemorySearchResult> Memories,
    PersonalityProfile? Profile);
