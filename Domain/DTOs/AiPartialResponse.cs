namespace Domain.DTOs;

public record AiPartialResponse
{
    public required string Content { get; init; } = string.Empty;
    public bool IsFinal { get; init; } = false;
    public required string CorrelationId { get; init; }
}