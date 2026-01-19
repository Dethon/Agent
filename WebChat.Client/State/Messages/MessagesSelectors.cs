using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

/// <summary>
/// Selector factories for deriving data from MessagesState.
/// Factory methods are used because selectors need to capture the topicId parameter.
/// </summary>
public static class MessagesSelectors
{
    /// <summary>
    /// Creates a selector that returns messages for a specific topic.
    /// Returns empty list if topic not found.
    /// </summary>
    public static Selector<MessagesState, IReadOnlyList<ChatMessageModel>> MessagesForTopic(string topicId) =>
        Selector.Create<MessagesState, IReadOnlyList<ChatMessageModel>>(state =>
            state.MessagesByTopic.GetValueOrDefault(topicId, []));

    /// <summary>
    /// Creates a selector that checks if a topic has been loaded.
    /// </summary>
    public static Selector<MessagesState, bool> HasMessagesForTopic(string topicId) =>
        Selector.Create<MessagesState, bool>(state =>
            state.LoadedTopics.Contains(topicId));

    /// <summary>
    /// Creates a selector that returns the message count for a specific topic.
    /// </summary>
    public static Selector<MessagesState, int> MessageCount(string topicId) =>
        Selector.Create<MessagesState, int>(state =>
            state.MessagesByTopic.GetValueOrDefault(topicId, []).Count);

    /// <summary>
    /// Returns the total number of messages across all topics.
    /// </summary>
    public static Selector<MessagesState, int> TotalMessageCount { get; } =
        Selector.Create<MessagesState, int>(state =>
            state.MessagesByTopic.Values.Sum(messages => messages.Count));

    /// <summary>
    /// Returns the number of loaded topics.
    /// </summary>
    public static Selector<MessagesState, int> LoadedTopicCount { get; } =
        Selector.Create<MessagesState, int>(state => state.LoadedTopics.Count);
}
