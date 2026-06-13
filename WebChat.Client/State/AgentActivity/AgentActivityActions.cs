namespace WebChat.Client.State.AgentActivity;

public record AllAgentsTopicsMapped(IReadOnlyDictionary<string, string> TopicToAgent) : IAction;

public record MarkAgentUnseenActivity(string AgentId) : IAction;

public record ClearAgentUnseenActivity(string AgentId) : IAction;