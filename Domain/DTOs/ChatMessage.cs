namespace Domain.DTOs;

public record ChatMessage
{
    public required ChatMessageRole Role { get; init; }
    public required string Content { get; init; }
}

public enum ChatMessageRole
{
    User,
    Tool,
    System
}