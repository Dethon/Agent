namespace WebChat.Client.State.SubAgents;

public sealed record SubAgentCardKey(string TopicId, string Handle);

public sealed record SubAgentCardState(
    string Handle,
    string SubAgentId,
    string Status,
    string TopicId);
