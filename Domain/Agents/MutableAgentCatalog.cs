using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Domain.Agents;

public sealed class MutableAgentCatalog : IAgentCatalog
{
    private volatile IReadOnlyList<AgentCatalogEntry> _agents = [];

    public IReadOnlyList<AgentCatalogEntry> GetAll() => _agents;

    public AgentCatalogEntry? Get(string agentId) => _agents.FirstOrDefault(a => a.Id == agentId);

    public bool Exists(string agentId) => _agents.Any(a => a.Id == agentId);

    public void Replace(IReadOnlyList<AgentCatalogEntry> agents) => _agents = [.. agents];
}