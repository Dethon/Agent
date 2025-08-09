namespace Domain.DTOs;

public record ChatMessage
{
    public ChatMessageRole Role { get; init; }
    public string Content { get; init; }
}

public enum ChatMessageRole
{
    User,
    Tool,
    System
}