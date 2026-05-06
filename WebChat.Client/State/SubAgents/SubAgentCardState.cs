namespace WebChat.Client.State.SubAgents;

public sealed record SubAgentCardState(
    string Handle,
    string SubAgentId,
    string Status,
    string TopicId);
