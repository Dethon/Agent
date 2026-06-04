using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ChannelMessageNotification
{
    public required string ConversationId { get; init; }
    public required string Sender { get; init; }
    public required string Content { get; init; }
    public string? AgentId { get; init; }
    public IReadOnlyList<ReplyTarget>? ReplyTo { get; init; }
    public MessageOrigin? Origin { get; init; }
    public string? Location { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}