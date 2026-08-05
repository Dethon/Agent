using System.Collections.Immutable;

namespace WebChat.Client.State.AgentActivity;

public record AllAgentsTopicsMapped(IReadOnlyDictionary<string, string> TopicToAgent) : IAction;

public record MarkAgentUnseenActivity(string AgentId) : IAction;

public record ClearAgentUnseenActivity(string AgentId) : IAction;

public sealed class AgentActivityStore : IDisposable
{
    private readonly Store<AgentActivityState> _store;

    public AgentActivityStore(Dispatcher dispatcher)
    {
        _store = new Store<AgentActivityState>(AgentActivityState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public AgentActivityState State => _store.State;
    public IObservable<AgentActivityState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    private static AgentActivityState Reduce(AgentActivityState state, IAction action) => action switch
    {
        AllAgentsTopicsMapped a => state with
        {
            TopicToAgent = a.TopicToAgent.ToImmutableDictionary()
        },

        MarkAgentUnseenActivity a => state with
        {
            AgentsWithUnseenActivity = state.AgentsWithUnseenActivity.Add(a.AgentId)
        },

        ClearAgentUnseenActivity a => state with
        {
            AgentsWithUnseenActivity = state.AgentsWithUnseenActivity.Remove(a.AgentId)
        },

        _ => state
    };
}