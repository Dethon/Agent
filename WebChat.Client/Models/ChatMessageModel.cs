namespace WebChat.Client.Models;

public record ChatMessageModel
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public bool IsError { get; init; }

    public string? SenderId { get; init; }
    public string? SenderUsername { get; init; }
    public string? SenderAvatarUrl { get; init; }

    public bool HasContent =>
        !string.IsNullOrEmpty(Content) ||
        !string.IsNullOrEmpty(ToolCalls) ||
        !string.IsNullOrEmpty(Reasoning);
}