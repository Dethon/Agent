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
    // Optional originating room for room-aware prompts. Part of the shared channel protocol but
    // currently only populated by the voice channel; other channels leave it null.
    public string? Location { get; init; }
    // Optional originating voice satellite id. Like Location, part of the shared protocol but
    // currently only populated by the voice channel; other channels leave it null.
    public string? SatelliteId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}