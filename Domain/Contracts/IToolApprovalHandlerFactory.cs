using Domain.Agents;

namespace Domain.Contracts;

public interface IToolApprovalHandlerFactory
{
    IToolApprovalHandler Create(AgentKey agentKey);
}