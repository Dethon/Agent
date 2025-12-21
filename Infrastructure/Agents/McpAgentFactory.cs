using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public sealed class McpAgentFactory(
    IChatClient chatClient,
    string[] mcpEndpoints,
    string agentName,
    string agentDescription,
    IToolApprovalHandlerFactory approvalHandlerFactory,
    IEnumerable<string>? whitelistPatterns = null) : IAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey)
    {
        var name = $"{agentName}-{agentKey.ChatId}-{agentKey.ThreadId}";

        var handler = approvalHandlerFactory.Create(agentKey);
        var effectiveClient = new ToolApprovalChatClient(chatClient, handler, whitelistPatterns);

        return new McpAgent(mcpEndpoints, effectiveClient, name, agentDescription);
    }
}