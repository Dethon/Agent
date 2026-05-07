using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSessionManagerFactory(
    SystemChannelConnection systemChannel,
    IMetricsPublisher? metricsPublisher = null,
    ILogger<SubAgentSessionManager>? logger = null)
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
            metricsPublisher: metricsPublisher,
            logger: logger);
}
