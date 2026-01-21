namespace WebChat.Client.State.Streaming;

public static class StreamingReducers
{
    public static StreamingState Reduce(StreamingState state, IAction action)
    {
        return action switch
        {
            StreamStarted a => state with
            {
                StreamingTopics = new HashSet<string>(state.StreamingTopics) { a.TopicId },
                StreamingByTopic = new Dictionary<string, StreamingContent>(state.StreamingByTopic)
                {
                    [a.TopicId] = new()
                }
            },

            StreamChunk a => state with
            {
                StreamingByTopic = UpdateStreamingContent(state.StreamingByTopic, a)
            },

            StreamCompleted a => RemoveStreaming(state, a.TopicId),

            StreamCancelled a => RemoveStreaming(state, a.TopicId),

            StreamError a => state with
            {
                StreamingByTopic = SetError(state.StreamingByTopic, a.TopicId)
            },

            StartResuming a => state with
            {
                ResumingTopics = new HashSet<string>(state.ResumingTopics) { a.TopicId }
            },

            StopResuming a => state with
            {
                ResumingTopics = new HashSet<string>(state.ResumingTopics.Where(t => t != a.TopicId))
            },

            _ => state
        };
    }

    private static IReadOnlyDictionary<string, StreamingContent> UpdateStreamingContent(
        IReadOnlyDictionary<string, StreamingContent> streamingByTopic,
        StreamChunk chunk)
    {
        var existing = streamingByTopic.GetValueOrDefault(chunk.TopicId) ?? new StreamingContent();

        // StreamChunk contains the FULL accumulated content from the service,
        // so we replace (not accumulate) the state
        var updated = existing with
        {
            Content = chunk.Content ?? existing.Content,
            Reasoning = chunk.Reasoning ?? existing.Reasoning,
            ToolCalls = chunk.ToolCalls ?? existing.ToolCalls,
            CurrentMessageId = chunk.MessageId ?? existing.CurrentMessageId
        };

        return new Dictionary<string, StreamingContent>(streamingByTopic)
        {
            [chunk.TopicId] = updated
        };
    }

    private static IReadOnlyDictionary<string, StreamingContent> SetError(
        IReadOnlyDictionary<string, StreamingContent> streamingByTopic,
        string topicId)
    {
        var existing = streamingByTopic.GetValueOrDefault(topicId) ?? new StreamingContent();

        return new Dictionary<string, StreamingContent>(streamingByTopic)
        {
            [topicId] = existing with { IsError = true }
        };
    }

    private static StreamingState RemoveStreaming(StreamingState state, string topicId)
    {
        var newStreamingByTopic = new Dictionary<string, StreamingContent>(state.StreamingByTopic);
        newStreamingByTopic.Remove(topicId);

        return state with
        {
            StreamingTopics = new HashSet<string>(state.StreamingTopics.Where(t => t != topicId)),
            StreamingByTopic = newStreamingByTopic
        };
    }
}