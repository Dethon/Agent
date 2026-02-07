using Domain.Agents;
using Domain.DTOs;

namespace Domain.Contracts;

public interface IScheduleAgentFactory
{
    DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition);
}
