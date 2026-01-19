using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

/// <summary>
/// Memoized selectors for deriving data from TopicsState.
/// </summary>
public static class TopicsSelectors
{
    /// <summary>
    /// Derives the full StoredTopic from SelectedTopicId.
    /// Returns null if no topic is selected or topic not found.
    /// </summary>
    public static Selector<TopicsState, StoredTopic?> SelectedTopic { get; } =
        Selector.Create<TopicsState, StoredTopic?>(state =>
            state.SelectedTopicId is not null
                ? state.Topics.FirstOrDefault(t => t.TopicId == state.SelectedTopicId)
                : null);

    /// <summary>
    /// Filters topics by the currently selected agent.
    /// Returns all topics if no agent is selected.
    /// </summary>
    public static Selector<TopicsState, IReadOnlyList<StoredTopic>> TopicsForSelectedAgent { get; } =
        Selector.Create<TopicsState, IReadOnlyList<StoredTopic>>(state =>
            state.SelectedAgentId is not null
                ? state.Topics.Where(t => t.AgentId == state.SelectedAgentId).ToList()
                : state.Topics.ToList());

    /// <summary>
    /// Creates a selector that filters topics by a specific agent ID.
    /// </summary>
    public static Selector<TopicsState, IReadOnlyList<StoredTopic>> TopicsForAgent(string agentId) =>
        Selector.Create<TopicsState, IReadOnlyList<StoredTopic>>(state =>
            state.Topics.Where(t => t.AgentId == agentId).ToList());

    /// <summary>
    /// Returns the count of topics.
    /// </summary>
    public static Selector<TopicsState, int> TopicCount { get; } =
        Selector.Create<TopicsState, int>(state => state.Topics.Count);

    /// <summary>
    /// Returns whether any topics exist.
    /// </summary>
    public static Selector<TopicsState, bool> HasTopics { get; } =
        Selector.Create<TopicsState, bool>(state => state.Topics.Count > 0);
}
