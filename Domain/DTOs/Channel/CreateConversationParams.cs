using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record CreateConversationParams
{
    public required string AgentId { get; init; }
    public required string TopicName { get; init; }
    public required string Sender { get; init; }
}