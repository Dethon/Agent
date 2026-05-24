using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IAgentCatalog
{
    IReadOnlyList<AgentCatalogEntry> GetAll();
    AgentCatalogEntry? Get(string agentId);
    bool Exists(string agentId);
    void Replace(IReadOnlyList<AgentCatalogEntry> agents);
}