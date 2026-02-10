namespace Domain.DTOs.WebChat;

public record TopicMetadata(
    string TopicId,
    long ChatId,
    long ThreadId,
    string AgentId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    string? LastReadMessageId = null,
    string SpaceSlug = "default");