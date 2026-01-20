namespace WebChat.Client.State.Streaming;

public static class StreamingSelectors
{
    public static Func<StreamingState, StreamingContent?> SelectStreamingContent(string topicId)
        => state => state.StreamingByTopic.GetValueOrDefault(topicId);


    public static Func<StreamingState, bool> SelectIsStreaming(string topicId)
        => state => state.StreamingTopics.Contains(topicId);


    public static Func<StreamingState, IReadOnlySet<string>> SelectStreamingTopics()
        => state => state.StreamingTopics;
}