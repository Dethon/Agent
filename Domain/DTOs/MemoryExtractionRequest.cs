namespace Domain.DTOs;

public record MemoryExtractionRequest(
    string UserId,
    string ThreadStateKey,
    int AnchorIndex,
    string? ConversationId,
    string? AgentId)
{
    public string? FallbackContent { get; init; }
}
