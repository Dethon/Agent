namespace WebChat.Client.State.Streaming;

/// <summary>
/// Selector factories for topic-scoped streaming content selection.
/// These return selector functions that can be composed with observables.
/// </summary>
public static class StreamingSelectors
{
    /// <summary>
    /// Creates a selector that extracts streaming content for a specific topic.
    /// Returns null if the topic is not currently streaming.
    /// </summary>
    public static Func<StreamingState, StreamingContent?> SelectStreamingContent(string topicId)
        => state => state.StreamingByTopic.GetValueOrDefault(topicId);

    /// <summary>
    /// Creates a selector that returns whether a specific topic is streaming.
    /// </summary>
    public static Func<StreamingState, bool> SelectIsStreaming(string topicId)
        => state => state.StreamingTopics.Contains(topicId);

    /// <summary>
    /// Returns a selector for the set of all currently streaming topics.
    /// </summary>
    public static Func<StreamingState, IReadOnlySet<string>> SelectStreamingTopics()
        => state => state.StreamingTopics;
}
