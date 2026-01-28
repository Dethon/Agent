namespace WebChat.Client.State.Pipeline;

public sealed record ManagedMessage
{
    public required string Id { get; init; }
    public required string TopicId { get; init; }
    public required MessageLifecycle State { get; init; }
    public required string Role { get; init; }
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? SenderId { get; init; }

    public bool HasContent =>
        !string.IsNullOrEmpty(Content) ||
        !string.IsNullOrEmpty(Reasoning) ||
        !string.IsNullOrEmpty(ToolCalls);
}
