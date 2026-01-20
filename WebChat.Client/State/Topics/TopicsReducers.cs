namespace WebChat.Client.State.Topics;

/// <summary>
/// Pure reducer functions for TopicsState.
/// All mutations create new collections - never mutate existing state.
/// </summary>
public static class TopicsReducers
{
    /// <summary>
    /// Reduce TopicsState based on the dispatched action.
    /// Returns unchanged state for unhandled actions.
    /// </summary>
    public static TopicsState Reduce(TopicsState state, IAction action) => action switch
    {
        LoadTopics => state with
        {
            IsLoading = true,
            Error = null
        },

        TopicsLoaded a => state with
        {
            Topics = a.Topics,
            IsLoading = false,
            Error = null // Auto-clear on success
        },

        SelectTopic a => state with
        {
            SelectedTopicId = a.TopicId
        },

        AddTopic a => state.Topics.Any(t => t.TopicId == a.Topic.TopicId)
            ? state  // Topic already exists, ignore duplicate
            : state with
            {
                Topics = state.Topics.Append(a.Topic).ToList(),
                Error = null // Auto-clear on success
            },

        UpdateTopic a => state with
        {
            Topics = state.Topics
                .Select(t => t.TopicId == a.Topic.TopicId ? a.Topic : t)
                .ToList(),
            Error = null // Auto-clear on success
        },

        RemoveTopic a => state with
        {
            Topics = state.Topics
                .Where(t => t.TopicId != a.TopicId)
                .ToList(),
            // Clear selection if the removed topic was selected
            SelectedTopicId = state.SelectedTopicId == a.TopicId ? null : state.SelectedTopicId,
            Error = null // Auto-clear on success
        },

        SetAgents a => state with
        {
            Agents = a.Agents,
            Error = null // Auto-clear on success
        },

        SelectAgent a => state with
        {
            SelectedAgentId = a.AgentId
        },

        TopicsError a => state with
        {
            Error = a.Message,
            IsLoading = false
        },

        CreateNewTopic => state with
        {
            SelectedTopicId = null
        },

        _ => state
    };
}
