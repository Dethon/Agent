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
            Error = null
        },

        SelectTopic a => state with
        {
            SelectedTopicId = a.TopicId
        },

        AddTopic a => state.Topics.Any(t => t.TopicId == a.Topic.TopicId)
            ? state
            : state with
            {
                Topics = state.Topics.Append(a.Topic).ToList(),
                Error = null
            },

        UpdateTopic a => state with
        {
            Topics = state.Topics
                .Select(t => t.TopicId == a.Topic.TopicId ? a.Topic : t)
                .ToList(),
            Error = null
        },

        RemoveTopic a => state with
        {
            Topics = state.Topics
                .Where(t => t.TopicId != a.TopicId)
                .ToList(),
            SelectedTopicId = state.SelectedTopicId == a.TopicId ? null : state.SelectedTopicId,
            Error = null
        },

        SetAgents a => state with
        {
            Agents = a.Agents,
            // A live catalog refresh may drop the selected agent; fall back to the first
            // available (or null when empty) so the UI never points at a ghost agent.
            SelectedAgentId = state.SelectedAgentId is not null && a.Agents.All(ag => ag.Id != state.SelectedAgentId)
                ? a.Agents.FirstOrDefault()?.Id
                : state.SelectedAgentId,
            Error = null
        },

        SelectAgent a => state with
        {
            SelectedAgentId = a.AgentId,
            SelectedTopicId = null
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