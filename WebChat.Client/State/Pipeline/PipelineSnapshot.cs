namespace WebChat.Client.State.Pipeline;

public sealed record PipelineSnapshot(
    string? StreamingMessageId,
    int FinalizedCount,
    int PendingUserMessages);