using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Clients;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public sealed class McpAgentFactory(
    IChatClient chatClient,
    string[] mcpEndpoints,
    string agentName,
    string agentDescription,
    TelegramToolApprovalHandler? approvalHandler = null,
    IEnumerable<string>? whitelistedTools = null) : IAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey)
    {
        var name = $"{agentName}-{agentKey.ChatId}-{agentKey.ThreadId}";

        approvalHandler?.SetActiveChat(agentKey.ChatId, Convert.ToInt32(agentKey.ThreadId));

        var effectiveClient = approvalHandler is not null
            ? new ToolApprovalChatClient(chatClient, approvalHandler, whitelistedTools)
            : chatClient;
        return new McpAgent(mcpEndpoints, effectiveClient, name, agentDescription);
    }
}