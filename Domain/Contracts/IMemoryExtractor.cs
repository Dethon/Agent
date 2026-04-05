using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IMemoryExtractor
{
    Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        IReadOnlyList<ChatMessage> contextWindow, string userId, CancellationToken ct);
}
