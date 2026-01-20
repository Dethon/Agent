using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public static class MessagesSelectors
{
    public static Selector<MessagesState, IReadOnlyList<ChatMessageModel>> MessagesForTopic(string topicId) =>
        Selector.Create<MessagesState, IReadOnlyList<ChatMessageModel>>(state =>
            state.MessagesByTopic.GetValueOrDefault(topicId, []));


    public static Selector<MessagesState, bool> HasMessagesForTopic(string topicId) =>
        Selector.Create<MessagesState, bool>(state =>
            state.LoadedTopics.Contains(topicId));


    public static Selector<MessagesState, int> MessageCount(string topicId) =>
        Selector.Create<MessagesState, int>(state =>
            state.MessagesByTopic.GetValueOrDefault(topicId, []).Count);


    public static Selector<MessagesState, int> TotalMessageCount { get; } =
        Selector.Create<MessagesState, int>(state =>
            state.MessagesByTopic.Values.Sum(messages => messages.Count));


    public static Selector<MessagesState, int> LoadedTopicCount { get; } =
        Selector.Create<MessagesState, int>(state => state.LoadedTopics.Count);
}