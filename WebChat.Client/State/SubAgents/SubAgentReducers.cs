namespace WebChat.Client.State.SubAgents;

public static class SubAgentReducers
{
    public static SubAgentState Reduce(SubAgentState state, IAction action) => action switch
    {
        SubAgentAnnounced announced => state with
        {
            Cards = Upsert(state.Cards,
                new SubAgentCardKey(announced.TopicId, announced.Handle),
                new SubAgentCardState(announced.Handle, announced.SubAgentId, "Running", announced.TopicId))
        },
        SubAgentUpdated updated when state.Cards.TryGetValue(
            new SubAgentCardKey(updated.TopicId, updated.Handle), out var existing) => state with
        {
            Cards = Upsert(state.Cards,
                new SubAgentCardKey(updated.TopicId, updated.Handle),
                existing with { Status = updated.Status })
        },
        SubAgentRemoved removed => state with
        {
            Cards = state.Cards
                .Where(kv => kv.Key != new SubAgentCardKey(removed.TopicId, removed.Handle))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        },
        _ => state
    };

    private static IReadOnlyDictionary<SubAgentCardKey, SubAgentCardState> Upsert(
        IReadOnlyDictionary<SubAgentCardKey, SubAgentCardState> cards,
        SubAgentCardKey key,
        SubAgentCardState value)
    {
        var dict = new Dictionary<SubAgentCardKey, SubAgentCardState>(cards) { [key] = value };
        return dict;
    }
}
