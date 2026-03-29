namespace Domain.DTOs;

public record MemoryExtractionRequest(
    string UserId,
    string MessageContent,
    string? ConversationId,
    string? AgentId);
