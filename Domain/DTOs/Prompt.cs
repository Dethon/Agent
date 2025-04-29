namespace Domain.DTOs;

public record ChatPrompt
{
    public required string Prompt { get; init; }
    public required long ChatId { get; init; }
    public required int MessageId { get; init; }
    public int? ReplyToMessageId { get; init; }
}