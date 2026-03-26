using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ChannelMessage
{
    public required string ConversationId { get; init; }
    public required string Content { get; init; }
    public required string Sender { get; init; }
    public required string ChannelId { get; init; }
    public string? AgentId { get; init; }
}