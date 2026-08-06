namespace WebChat.Client.State.Pipeline;

public sealed record PipelineSnapshot(
    int FinalizedCount,
    int PendingUserMessages);