using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

[UsedImplicitly]
public record ChatHistoryMessage(
    string? MessageId,
    string Role,
    string Content,
    string? SenderId,
    DateTimeOffset? Timestamp);