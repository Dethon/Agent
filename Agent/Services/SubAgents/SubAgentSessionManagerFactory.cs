using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSessionManagerFactory(
    SystemChannelConnection systemChannel,
    IMetricsPublisher? metricsPublisher = null)
{
    public ISubAgentSessions Create(
        AgentKey agentKey,
        Func<SubAgentDefinition, DisposableAgent> agentFactory,
        string replyToConversationId) =>
        new SubAgentSessionManager(
            agentFactory: agentFactory,
            replyToConversationId: replyToConversationId,
            replyChannel: null,
            systemChannel: systemChannel,
            agentKey: agentKey,
            metricsPublisher: metricsPublisher);
}
