using Domain.DTOs;

namespace Domain.Contracts;

public interface IMemoryExtractor
{
    Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        string messageContent, string userId, CancellationToken ct);
}
