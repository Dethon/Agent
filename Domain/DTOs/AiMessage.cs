namespace Domain.DTOs;

public record AiMessage
{
    public required AiMessageRole Role { get; init; }
    public required string Content { get; init; }
}

public enum AiMessageRole
{
    User,
    Tool,
    System,
    Assistant
}