using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Contracts;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSessionsRegistry : ISubAgentSessionsRegistry, IAsyncDisposable
{
    private readonly ConcurrentDictionary<AgentKey, SubAgentSessionManager> _byKey = new();
    private readonly ConcurrentDictionary<string, AgentKey> _byConversation = new();
    private readonly Func<AgentKey, SubAgentSessionManager> _factory;

    public SubAgentSessionsRegistry(Func<AgentKey, SubAgentSessionManager> factory)
    {
        _factory = factory;
    }

    public ISubAgentSessions GetOrCreate(AgentKey key)
    {
        var mgr = _byKey.GetOrAdd(key, k => _factory(k));
        _byConversation.TryAdd(key.ConversationId, key);
        return mgr;
    }

    public ISubAgentSessions GetOrCreateExplicit(AgentKey key, Func<ISubAgentSessions> factory)
    {
        var mgr = _byKey.GetOrAdd(key, _ => (SubAgentSessionManager)factory());
        _byConversation.TryAdd(key.ConversationId, key);
        return mgr;
    }

    public bool TryGet(AgentKey key, out ISubAgentSessions sessions)
    {
        if (_byKey.TryGetValue(key, out var mgr)) { sessions = mgr; return true; }
        sessions = null!;
        return false;
    }

    public bool TryGetByConversation(string conversationId, out ISubAgentSessions sessions)
    {
        if (_byConversation.TryGetValue(conversationId, out var key) &&
            _byKey.TryGetValue(key, out var mgr))
        {
            sessions = mgr;
            return true;
        }
        sessions = null!;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in _byKey.Values) await m.DisposeAsync();
        _byKey.Clear();
        _byConversation.Clear();
    }
}
