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

    // Which turn this reply answers. A conversation outlives a turn, so a channel that has to know
    // whether an arriving reply is the one its user is waiting for cannot ask the conversation —
    // it compares this against the key the turn was dispatched under. Null only if the echo broke.
    public string? TurnKey { get; init; }

    // Whether the turn this reply answers was started by the agent rather than by the user — a
    // timer, an alarm, a scheduled message. A channel that finds a key it does not recognise needs
    // this to tell a delivery that merely landed mid-conversation from an answer it gave up on.
    public bool? AgentInitiated { get; init; }
}