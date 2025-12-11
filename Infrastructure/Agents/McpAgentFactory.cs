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
    public DisposableAgent CreateAsync(AgentKey agentKey)
    {
        var name = $"{agentName}-{agentKey.ChatId}-{agentKey.ThreadId}";
        return new McpAgent(mcpEndpoints, chatClient, name, agentDescription);
    }
}