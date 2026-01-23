using System.Collections.Immutable;

namespace WebChat.Client.State.Streaming;

public static class StreamingReducers
{
    public static StreamingState Reduce(StreamingState state, IAction action) => action switch
    {
        StreamStarted a => state with
        {
            StreamingTopics = state.StreamingTopics.Add(a.TopicId),
            StreamingByTopic = state.StreamingByTopic.SetItem(a.TopicId, new StreamingContent())
        },

        StreamChunk a => state with
        {
            StreamingByTopic = UpdateStreamingContent(state.StreamingByTopic, a)
        },

        StreamCompleted a => RemoveStreaming(state, a.TopicId),

        StreamCancelled a => RemoveStreaming(state, a.TopicId),

        ResetStreamingContent a => state with
        {
            StreamingByTopic = state.StreamingByTopic.SetItem(a.TopicId, new StreamingContent())
        },

        StreamError a => state with
        {
            StreamingByTopic = SetError(state.StreamingByTopic, a.TopicId)
        },

        StartResuming a => state with
        {
            ResumingTopics = state.ResumingTopics.Add(a.TopicId)
        },

        StopResuming a => state with
        {
            ResumingTopics = state.ResumingTopics.Remove(a.TopicId)
        },

        _ => state
    };

    private static ImmutableDictionary<string, StreamingContent> UpdateStreamingContent(
        ImmutableDictionary<string, StreamingContent> streamingByTopic,
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

        return streamingByTopic.SetItem(chunk.TopicId, updated);
    }

    private static ImmutableDictionary<string, StreamingContent> SetError(
        ImmutableDictionary<string, StreamingContent> streamingByTopic,
        string topicId)
    {
        var existing = streamingByTopic.GetValueOrDefault(topicId) ?? new StreamingContent();
        return streamingByTopic.SetItem(topicId, existing with { IsError = true });
    }

    private static StreamingState RemoveStreaming(StreamingState state, string topicId)
    {
        return state with
        {
            StreamingTopics = state.StreamingTopics.Remove(topicId),
            StreamingByTopic = state.StreamingByTopic.Remove(topicId)
        };
    }
}