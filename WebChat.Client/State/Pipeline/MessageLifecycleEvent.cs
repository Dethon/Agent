namespace WebChat.Client.State.Pipeline;

public sealed record MessageLifecycleEvent(
    string TopicId,
    string MessageId,
    MessageLifecycle FromState,
    MessageLifecycle ToState,
    string Source,
    DateTimeOffset Timestamp);
