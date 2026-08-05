namespace WebChat.Client.State.AgentSettings;

public record SetAgentModel(string AgentId, string? Model) : IAction;

public record SetAgentReasoningEffort(string AgentId, string? Effort) : IAction;

public record AgentSettingsLoaded(string AgentId, AgentModelSettings Settings) : IAction;

public sealed class AgentSettingsStore : IDisposable
{
    private readonly Store<AgentSettingsState> _store;

    public AgentSettingsStore(Dispatcher dispatcher)
    {
        _store = new Store<AgentSettingsState>(AgentSettingsState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public AgentSettingsState State => _store.State;
    public IObservable<AgentSettingsState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    private static AgentSettingsState Reduce(AgentSettingsState state, IAction action) => action switch
    {
        SetAgentModel a => WithEntry(state, a.AgentId, Current(state, a.AgentId) with { Model = a.Model }),

        SetAgentReasoningEffort a =>
            WithEntry(state, a.AgentId, Current(state, a.AgentId) with { ReasoningEffort = a.Effort }),

        AgentSettingsLoaded a => WithEntry(state, a.AgentId, a.Settings),

        _ => state
    };

    private static AgentModelSettings Current(AgentSettingsState state, string agentId) =>
        state.ByAgent.GetValueOrDefault(agentId) ?? new AgentModelSettings(null, null);

    private static AgentSettingsState WithEntry(
        AgentSettingsState state, string agentId, AgentModelSettings settings) =>
        state with { ByAgent = state.ByAgent.With(agentId, settings) };
}