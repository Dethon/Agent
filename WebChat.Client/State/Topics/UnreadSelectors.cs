using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State.Topics;

// Lifted verbatim from TopicList to a single shared, testable source of truth.
// Behavior is intentionally unchanged (it touches the fragile streaming bookkeeping).
public static class UnreadSelectors
{
    public static IReadOnlyDictionary<string, int> ComputeUnreadCounts(
        MessagesState messagesState,
        TopicsState topicsState,
        StreamingState streamingState)
    {
        var result = new Dictionary<string, int>();
        foreach (var topic in topicsState.Topics)
        {
            if (topic.TopicId == topicsState.SelectedTopicId)
            {
                continue;
            }

            var messages = messagesState.MessagesByTopic.GetValueOrDefault(topic.TopicId, []);
            var hasStreamingContent = streamingState.StreamingByTopic.TryGetValue(topic.TopicId, out var streaming)
                                      && streaming.HasContent;

            if (messages.Count == 0 && !hasStreamingContent)
            {
                continue;
            }

            var hasStreamingMessageId = hasStreamingContent && streaming?.CurrentMessageId is not null;
            List<ChatMessageModel> allMessages = hasStreamingMessageId
                ? [.. messages, new ChatMessageModel { Role = "assistant", MessageId = streaming?.CurrentMessageId }]
                : [.. messages];

            var unreadCount = GetUnreadCountSince(allMessages, topic.LastReadMessageId);
            if (unreadCount > 0)
            {
                result[topic.TopicId] = unreadCount;
            }
        }
        return result;
    }

    public static int GetUnreadCountSince(IReadOnlyList<ChatMessageModel> messages, string? lastReadMessageId)
    {
        if (lastReadMessageId is null)
        {
            return messages.Count;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].MessageId == lastReadMessageId)
            {
                return messages.Count - 1 - i;
            }
        }

        // ID not found — treat all as unread
        return messages.Count;
    }
}