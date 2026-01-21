namespace WebChat.Client.State.Streaming;

public record StreamStarted(string TopicId) : IAction;

public record StreamChunk(
    string TopicId,
    string? Content,
    string? Reasoning,
    string? ToolCalls,
    string? MessageId) : IAction;

public record StreamCompleted(string TopicId) : IAction;

public record StreamCancelled(string TopicId) : IAction;

public record StreamError(string TopicId, string Error) : IAction;

public record StartResuming(string TopicId) : IAction;

public record StopResuming(string TopicId) : IAction;

// User-initiated actions dispatched from components
public record SendMessage(string? TopicId, string Message) : IAction;

public record CancelStreaming(string TopicId) : IAction;
