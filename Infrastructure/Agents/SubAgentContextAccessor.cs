using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Agents;

public sealed class SubAgentContextAccessor : ISubAgentContextAccessor
{
    private readonly ConcurrentDictionary<string, SubAgentContext> _contexts = new();

    public void SetContext(string agentName, SubAgentContext context) =>
        _contexts[agentName] = context;

    public SubAgentContext? GetContext(string agentName) =>
        _contexts.GetValueOrDefault(agentName);

    public void RemoveContext(string agentName) =>
        _contexts.TryRemove(agentName, out _);
}
