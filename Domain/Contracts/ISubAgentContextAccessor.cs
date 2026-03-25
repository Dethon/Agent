using Domain.DTOs;

namespace Domain.Contracts;

public interface ISubAgentContextAccessor
{
    void SetContext(string agentName, SubAgentContext context);
    SubAgentContext? GetContext(string agentName);
    void RemoveContext(string agentName);
}
