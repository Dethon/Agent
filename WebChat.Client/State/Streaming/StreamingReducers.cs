namespace WebChat.Client.State.Streaming;

public static class StreamingReducers
{
    public static StreamingState Reduce(StreamingState state, IAction action) => action switch
    {
        StreamStarted a => state with
        {
            StreamingTopics = new HashSet<string>(state.StreamingTopics) { a.TopicId },
            StreamingByTopic = new Dictionary<string, StreamingContent>(state.StreamingByTopic)
            {
                [a.TopicId] = new StreamingContent()
            }
        },

        StreamChunk a => state with
        {
            StreamingByTopic = AccumulateChunk(state.StreamingByTopic, a)
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

    private static IReadOnlyDictionary<string, StreamingContent> AccumulateChunk(
        IReadOnlyDictionary<string, StreamingContent> streamingByTopic,
        StreamChunk chunk)
    {
        var existing = streamingByTopic.GetValueOrDefault(chunk.TopicId) ?? new StreamingContent();

        var updated = existing with
        {
            Content = AccumulateString(existing.Content, chunk.Content),
            Reasoning = AccumulateString(existing.Reasoning, chunk.Reasoning),
            ToolCalls = AccumulateString(existing.ToolCalls, chunk.ToolCalls),
            CurrentMessageId = chunk.MessageId ?? existing.CurrentMessageId
        };

        return new Dictionary<string, StreamingContent>(streamingByTopic)
        {
            [chunk.TopicId] = updated
        };
    }

    private static string AccumulateString(string? existing, string? addition)
    {
        if (string.IsNullOrEmpty(addition))
            return existing ?? "";

        if (string.IsNullOrEmpty(existing))
            return addition;

        return existing + addition;
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
