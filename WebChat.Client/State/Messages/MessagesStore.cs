using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public record MessagesLoaded(string TopicId, IReadOnlyList<ChatMessageModel> Messages) : IAction;

public record AddMessage(string TopicId, ChatMessageModel Message, string? StreamMessageId = null) : IAction;

public record UpdateMessage(string TopicId, string MessageId, ChatMessageModel Message) : IAction;

public record RemoveLastMessage(string TopicId) : IAction;

public record ClearMessages(string TopicId) : IAction;

public record RemoveTrailingErrors(string TopicId) : IAction;

public record RetryLastMessage(string TopicId) : IAction;

public record ClearAllMessages : IAction;

public sealed class MessagesStore : IDisposable
{
    private readonly Store<MessagesState> _store;

    public MessagesStore(Dispatcher dispatcher)
    {
        _store = new Store<MessagesState>(MessagesState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public MessagesState State => _store.State;

    public IObservable<MessagesState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static MessagesState Reduce(MessagesState state, IAction action) => action switch
    {
        MessagesLoaded a => state with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
            {
                [a.TopicId] = a.Messages
            },
            LoadedTopics = new HashSet<string>(state.LoadedTopics) { a.TopicId },
            FinalizedMessageIdsByTopic = new Dictionary<string, IReadOnlySet<string>>(state.FinalizedMessageIdsByTopic)
            {
                [a.TopicId] = a.Messages
                    .Select(m => m.MessageId)
                    .Where(id => id is not null)
                    .ToHashSet()!
            }
        },

        AddMessage a => AddMessageWithDedup(state, a.TopicId, a.Message, a.StreamMessageId),

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

        RemoveTrailingErrors a when state.MessagesByTopic.TryGetValue(a.TopicId, out var msgs) =>
            state with
            {
                MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
                {
                    [a.TopicId] = msgs.Reverse().SkipWhile(m => m.IsError).Reverse().ToList()
                }
            },

        RemoveTrailingErrors => state,

        ClearMessages a => ClearTopicMessages(state, a.TopicId),

        ClearAllMessages => MessagesState.Initial,

        _ => state
    };

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

        var updated = messages
            .Select(m => m.MessageId == messageId ? updatedMessage : m)
            .ToList();

        if (updated.SequenceEqual(messages))
        {
            return messagesByTopic;
        }

        return new Dictionary<string, IReadOnlyList<ChatMessageModel>>(messagesByTopic)
        {
            [topicId] = updated
        };
    }

    private static MessagesState ClearTopicMessages(MessagesState state, string topicId)
    {
        var messages = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic);
        messages.Remove(topicId);

        var finalized = new Dictionary<string, IReadOnlySet<string>>(state.FinalizedMessageIdsByTopic);
        finalized.Remove(topicId);

        return state with
        {
            MessagesByTopic = messages,
            LoadedTopics = new HashSet<string>(state.LoadedTopics.Where(t => t != topicId)),
            FinalizedMessageIdsByTopic = finalized
        };
    }

    private static MessagesState AddMessageWithDedup(
        MessagesState state,
        string topicId,
        ChatMessageModel message,
        string? streamMessageId)
    {
        var existingMessages = state.MessagesByTopic.GetValueOrDefault(topicId, []);
        var finalizedIds = state.FinalizedMessageIdsByTopic.GetValueOrDefault(topicId)
                           ?? new HashSet<string>();

        // If a stream message ID is provided, check if it's already been finalized
        // This prevents duplicates from race conditions between HandleUserMessage
        // and StreamingService both trying to add the same assistant message
        if (!string.IsNullOrEmpty(streamMessageId) && finalizedIds.Contains(streamMessageId))
        {
            return state;
        }

        var newFinalizedIds = finalizedIds;
        if (!string.IsNullOrEmpty(streamMessageId))
        {
            newFinalizedIds = new HashSet<string>(finalizedIds) { streamMessageId };
        }

        return state with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
            {
                [topicId] = existingMessages.Append(message).ToList()
            },
            FinalizedMessageIdsByTopic = new Dictionary<string, IReadOnlySet<string>>(state.FinalizedMessageIdsByTopic)
            {
                [topicId] = newFinalizedIds
            }
        };
    }
}