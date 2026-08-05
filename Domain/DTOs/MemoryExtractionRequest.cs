using Domain.Memory;

namespace Domain.DTOs;

public record MemoryExtractionRequest(
    string UserId,
    string? ThreadStateKey,
    MemoryAnchor Anchor,
    string? ConversationId,
    string? AgentId)
{
    public string? FallbackContent { get; init; }
}