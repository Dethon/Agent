using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

public static class TopicsSelectors
{
    public static Selector<TopicsState, StoredTopic?> SelectedTopic { get; } =
        Selector.Create<TopicsState, StoredTopic?>(state =>
            state.SelectedTopicId is not null
                ? state.Topics.FirstOrDefault(t => t.TopicId == state.SelectedTopicId)
                : null);


    public static Selector<TopicsState, IReadOnlyList<StoredTopic>> TopicsForSelectedAgent { get; } =
        Selector.Create<TopicsState, IReadOnlyList<StoredTopic>>(state =>
            state.SelectedAgentId is not null
                ? state.Topics.Where(t => t.AgentId == state.SelectedAgentId).ToList()
                : state.Topics.ToList());


    public static Selector<TopicsState, IReadOnlyList<StoredTopic>> TopicsForAgent(string agentId) =>
        Selector.Create<TopicsState, IReadOnlyList<StoredTopic>>(state =>
            state.Topics.Where(t => t.AgentId == agentId).ToList());


    public static Selector<TopicsState, int> TopicCount { get; } =
        Selector.Create<TopicsState, int>(state => state.Topics.Count);


    public static Selector<TopicsState, bool> HasTopics { get; } =
        Selector.Create<TopicsState, bool>(state => state.Topics.Count > 0);
}