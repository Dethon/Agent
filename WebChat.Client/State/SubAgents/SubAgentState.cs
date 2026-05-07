namespace WebChat.Client.State.SubAgents;

public sealed record SubAgentState
{
    public IReadOnlyDictionary<SubAgentCardKey, SubAgentCardState> Cards { get; init; } =
        new Dictionary<SubAgentCardKey, SubAgentCardState>();

    public static SubAgentState Initial => new();
}
