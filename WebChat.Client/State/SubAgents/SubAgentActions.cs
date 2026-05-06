namespace WebChat.Client.State.SubAgents;

public record SubAgentAnnounced(string TopicId, string Handle, string SubAgentId) : IAction;

public record SubAgentUpdated(string TopicId, string Handle, string Status) : IAction;

public record SubAgentRemoved(string TopicId, string Handle) : IAction;
