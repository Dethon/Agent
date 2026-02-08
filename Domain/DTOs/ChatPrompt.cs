using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ChatPrompt
{
    public required string Prompt { get; init; }
    public required long ChatId { get; init; }
    public required int? ThreadId { get; init; }
    public required int MessageId { get; init; }
    public required string Sender { get; init; }
    public string? AgentId { get; init; }
    public MessageSource Source { get; init; } = MessageSource.WebUi;
}