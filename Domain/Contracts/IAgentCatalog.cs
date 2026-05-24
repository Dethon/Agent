using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IAgentCatalog
{
    IReadOnlyList<AgentCatalogEntry> GetAll();
    AgentCatalogEntry? Get(string agentId);
    bool Exists(string agentId);
}

public interface IMutableAgentCatalog : IAgentCatalog
{
    void Replace(IReadOnlyList<AgentCatalogEntry> agents);
}