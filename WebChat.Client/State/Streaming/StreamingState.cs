namespace WebChat.Client.State.Streaming;

public sealed record StreamingContent
{
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public string? CurrentMessageId { get; init; }
    public bool IsError { get; init; }
}

public sealed record StreamingState
{
    public IReadOnlyDictionary<string, StreamingContent> StreamingByTopic { get; init; }
        = new Dictionary<string, StreamingContent>();
    public IReadOnlySet<string> StreamingTopics { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ResumingTopics { get; init; } = new HashSet<string>();

    public static StreamingState Initial => new();
}
