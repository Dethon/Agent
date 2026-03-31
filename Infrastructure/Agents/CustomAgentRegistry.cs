using System.Collections.Concurrent;
using Domain.DTOs;

namespace Infrastructure.Agents;

public class CustomAgentRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AgentDefinition>> _agentsByUser = new();

    public void Add(string userId, AgentDefinition definition)
    {
        var userAgents = _agentsByUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, AgentDefinition>());
        userAgents[definition.Id] = definition;
    }

    public bool Remove(string userId, string agentId)
    {
        if (!_agentsByUser.TryGetValue(userId, out var userAgents))
            return false;

        return userAgents.TryRemove(agentId, out _);
    }

    public AgentDefinition? FindById(string agentId)
    {
        return _agentsByUser.Values
            .Select(userAgents => userAgents.GetValueOrDefault(agentId))
            .FirstOrDefault(def => def is not null);
    }

    public IReadOnlyList<AgentDefinition> GetByUser(string userId)
    {
        return _agentsByUser.TryGetValue(userId, out var userAgents)
            ? userAgents.Values.ToList()
            : [];
    }

}
