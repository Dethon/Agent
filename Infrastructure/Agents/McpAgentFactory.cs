using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public sealed class McpAgentFactory(
    IChatClient chatClient,
    string[] mcpEndpoints,
    string agentName,
    string agentDescription) : IAgentFactory
{
    public Task<DisposableAgent> CreateAsync(AgentKey agentKey, CancellationToken cancellationToken)
    {
        var name = $"{agentName}-{agentKey.ChatId}-{agentKey.ThreadId}";
        return McpAgent.CreateAsync(mcpEndpoints, chatClient, name, agentDescription, cancellationToken);
    }
}