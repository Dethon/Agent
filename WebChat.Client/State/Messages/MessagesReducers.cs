using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public static class MessagesReducers
{
    public static MessagesState Reduce(MessagesState state, IAction action) => action switch
    {
        MessagesLoaded a => state with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
            {
                [a.TopicId] = a.Messages
            },
            LoadedTopics = new HashSet<string>(state.LoadedTopics) { a.TopicId }
        },

        AddMessage a => state with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
            {
                [a.TopicId] = state.MessagesByTopic.GetValueOrDefault(a.TopicId, [])
                    .Append(a.Message)
                    .ToList()
            }
        },

        AddPendingMessage a => state with
        {
            PendingMessagesByTopic =
            new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.PendingMessagesByTopic)
            {
                [a.TopicId] = state.PendingMessagesByTopic.GetValueOrDefault(a.TopicId, [])
                    .Append(a.Message)
                    .ToList()
            }
        },

        FlushPendingMessages a => FlushPending(state, a.TopicId),

        UpdateMessage a => state with
        {
            MessagesByTopic = UpdateMessageInTopic(state.MessagesByTopic, a.TopicId, a.MessageId, a.Message)
        },

        RemoveLastMessage a when state.MessagesByTopic.TryGetValue(a.TopicId, out var messages) && messages.Count > 0 =>
            state with
            {
                MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
                {
                    [a.TopicId] = messages.Take(messages.Count - 1).ToList()
                }
            },

        RemoveLastMessage => state, // No messages to remove

        ClearMessages a => state with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
            {
                [a.TopicId] = []
            },
            PendingMessagesByTopic =
            new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.PendingMessagesByTopic)
            {
                [a.TopicId] = []
            },
            LoadedTopics = new HashSet<string>(state.LoadedTopics.Where(t => t != a.TopicId))
        },

        _ => state
    };

    private static MessagesState FlushPending(MessagesState state, string topicId)
    {
        var pending = state.PendingMessagesByTopic.GetValueOrDefault(topicId, []);
        if (pending.Count == 0)
        {
            return state;
        }

        var current = state.MessagesByTopic.GetValueOrDefault(topicId, []);
        var combined = current.Concat(pending).ToList();

        return state with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
            {
                [topicId] = combined
            },
            PendingMessagesByTopic =
            new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.PendingMessagesByTopic)
            {
                [topicId] = []
            }
        };
    }

    // ReSharper disable UnusedParameter.Local
    private static IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> UpdateMessageInTopic(
        IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> messagesByTopic,
        string topicId,
        string messageId,
        ChatMessageModel updatedMessage)
    {
        if (!messagesByTopic.TryGetValue(topicId, out var messages))
        {
            return messagesByTopic;
        }

        // Note: ChatMessageModel doesn't have a MessageId field currently
        // This action is designed for future use when message identity is needed
        // For now, it updates all messages (placeholder implementation)
        return new Dictionary<string, IReadOnlyList<ChatMessageModel>>(messagesByTopic)
        {
            [topicId] = messages.ToList() // No-op until MessageId is available
        };
    }
    // ReSharper restore UnusedParameter.Local
}