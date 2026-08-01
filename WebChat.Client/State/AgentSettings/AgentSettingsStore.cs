namespace WebChat.Client.State.AgentSettings;

public sealed class AgentSettingsStore : IDisposable
{
    private readonly Store<AgentSettingsState> _store;

    public AgentSettingsStore(Dispatcher dispatcher)
    {
        _store = new Store<AgentSettingsState>(AgentSettingsState.Initial);

        dispatcher.RegisterHandler<SetAgentModel>(action => _store.Dispatch(action, Reduce));
        dispatcher.RegisterHandler<SetAgentReasoningEffort>(action => _store.Dispatch(action, Reduce));
        dispatcher.RegisterHandler<AgentSettingsLoaded>(action => _store.Dispatch(action, Reduce));
    }

    public AgentSettingsState State => _store.State;
    public IObservable<AgentSettingsState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    private static AgentSettingsState Reduce(AgentSettingsState state, SetAgentModel action)
    {
        var current = state.ByAgent.GetValueOrDefault(action.AgentId) ?? new AgentModelSettings(null, null);
        return WithEntry(state, action.AgentId, current with { Model = action.Model });
    }

    private static AgentSettingsState Reduce(AgentSettingsState state, SetAgentReasoningEffort action)
    {
        var current = state.ByAgent.GetValueOrDefault(action.AgentId) ?? new AgentModelSettings(null, null);
        return WithEntry(state, action.AgentId, current with { ReasoningEffort = action.Effort });
    }

    private static AgentSettingsState Reduce(AgentSettingsState state, AgentSettingsLoaded action) =>
        WithEntry(state, action.AgentId, action.Settings);

    private static AgentSettingsState WithEntry(
        AgentSettingsState state, string agentId, AgentModelSettings settings)
    {
        var byAgent = state.ByAgent
            .Where(kv => kv.Key != agentId)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        byAgent[agentId] = settings;
        return state with { ByAgent = byAgent };
    }
}