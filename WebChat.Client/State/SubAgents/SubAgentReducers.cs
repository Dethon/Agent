namespace WebChat.Client.State.SubAgents;

public static class SubAgentReducers
{
    public static SubAgentState Reduce(SubAgentState state, IAction action) => action switch
    {
        SubAgentAnnounced announced => state with
        {
            Cards = Upsert(state.Cards, announced.Handle,
                new SubAgentCardState(announced.Handle, announced.SubAgentId, "Running", announced.TopicId))
        },
        SubAgentUpdated updated when state.Cards.TryGetValue(updated.Handle, out var existing)
            && existing.TopicId == updated.TopicId => state with
        {
            Cards = Upsert(state.Cards, updated.Handle, existing with { Status = updated.Status })
        },
        SubAgentRemoved removed => state with
        {
            Cards = state.Cards
                .Where(kv => !(kv.Value.TopicId == removed.TopicId && kv.Key == removed.Handle))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        },
        _ => state
    };

    private static IReadOnlyDictionary<string, SubAgentCardState> Upsert(
        IReadOnlyDictionary<string, SubAgentCardState> cards,
        string key,
        SubAgentCardState value)
    {
        var dict = new Dictionary<string, SubAgentCardState>(cards) { [key] = value };
        return dict;
    }
}
