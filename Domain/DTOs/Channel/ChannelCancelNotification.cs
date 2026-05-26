using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ChannelCancelNotification
{
    public required string ConversationId { get; init; }
    public string? AgentId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}