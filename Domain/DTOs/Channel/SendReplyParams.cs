using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record SendReplyParams
{
    public required string ConversationId { get; init; }
    public required string Content { get; init; }
    public required ReplyContentType ContentType { get; init; }
    public required bool IsComplete { get; init; }
    public string? MessageId { get; init; }
}