using Domain.DTOs;

namespace Domain.Contracts;

public interface IMemoryStore
{
    Task<MemoryEntry> StoreAsync(MemoryEntry memory, CancellationToken ct = default);
    Task<MemoryEntry?> GetByIdAsync(string userId, string memoryId, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> GetByUserIdAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string userId,
        string? query = null,
        float[]? queryEmbedding = null,
        IEnumerable<MemoryCategory>? categories = null,
        IEnumerable<string>? tags = null,
        double? minImportance = null,
        int limit = 10,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(string userId, string memoryId, CancellationToken ct = default);

    Task<bool> UpdateAccessAsync(string userId, string memoryId, CancellationToken ct = default);
    Task<bool> UpdateImportanceAsync(string userId, string memoryId, double importance, CancellationToken ct = default);
    Task<bool> SupersedeAsync(string userId, string oldMemoryId, string newMemoryId, CancellationToken ct = default);

    Task<PersonalityProfile?> GetProfileAsync(string userId, CancellationToken ct = default);
    Task<PersonalityProfile> SaveProfileAsync(PersonalityProfile profile, CancellationToken ct = default);

    Task<MemoryStats> GetStatsAsync(string userId, CancellationToken ct = default);
}

public record MemorySearchResult(MemoryEntry Memory, double Relevance);

public record MemoryStats(
    int TotalMemories,
    IReadOnlyDictionary<MemoryCategory, int> ByCategory);