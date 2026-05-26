namespace WebChat.Client.State.Topics;

public static class TopicsReducers
{
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
            ? state // Topic already exists, ignore duplicate
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
            // A live catalog refresh may drop the selected agent; fall back to the first
            // available (or null when empty) so the UI never points at a ghost agent.
            SelectedAgentId = state.SelectedAgentId is not null && a.Agents.All(ag => ag.Id != state.SelectedAgentId)
                ? a.Agents.FirstOrDefault()?.Id
                : state.SelectedAgentId,
            Error = null // Auto-clear on success
        },

        SelectAgent a => state with
        {
            SelectedAgentId = a.AgentId,
            SelectedTopicId = null // Clear topic selection when switching agents
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