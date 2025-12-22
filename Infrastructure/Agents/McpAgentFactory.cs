using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace Infrastructure.Agents;

public sealed class McpAgentFactory(
    IChatClient chatClient,
    string[] mcpEndpoints,
    string agentName,
    string agentDescription,
    IToolApprovalHandlerFactory approvalHandlerFactory,
    IConnectionMultiplexer redis,
    IEnumerable<string>? whitelistPatterns = null) : IAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey)
    {
        var name = $"{agentName}-{agentKey.ChatId}-{agentKey.ThreadId}";

        var handler = approvalHandlerFactory.Create(agentKey);
        var effectiveClient = new ToolApprovalChatClient(chatClient, handler, whitelistPatterns);
        var db = redis.GetDatabase();

        return new McpAgent(mcpEndpoints, effectiveClient, name, agentDescription, db);
    }
}