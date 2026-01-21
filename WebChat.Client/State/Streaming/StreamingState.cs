using System.Collections.Immutable;

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
    public ImmutableDictionary<string, StreamingContent> StreamingByTopic { get; init; }
        = ImmutableDictionary<string, StreamingContent>.Empty;

    public ImmutableHashSet<string> StreamingTopics { get; init; } = [];
    public ImmutableHashSet<string> ResumingTopics { get; init; } = [];

    public static StreamingState Initial => new();
}