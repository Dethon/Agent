namespace WebChat.Client.State.SubAgents;

public sealed record SubAgentState
{
    public IReadOnlyDictionary<string, SubAgentCardState> Cards { get; init; } =
        new Dictionary<string, SubAgentCardState>();

    public static SubAgentState Initial => new();
}
