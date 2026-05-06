using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Contracts;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSessionsRegistry : ISubAgentSessionsRegistry, IAsyncDisposable
{
    private readonly ConcurrentDictionary<AgentKey, SubAgentSessionManager> _byKey = new();
    private readonly Func<AgentKey, SubAgentSessionManager> _factory;

    public SubAgentSessionsRegistry(Func<AgentKey, SubAgentSessionManager> factory)
    {
        _factory = factory;
    }

    public ISubAgentSessions GetOrCreate(AgentKey key) =>
        _byKey.GetOrAdd(key, k => _factory(k));

    public bool TryGet(AgentKey key, out ISubAgentSessions sessions)
    {
        if (_byKey.TryGetValue(key, out var mgr)) { sessions = mgr; return true; }
        sessions = null!;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in _byKey.Values) await m.DisposeAsync();
        _byKey.Clear();
    }
}
