using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Agents;

public sealed class McpAgentFactory(
    IServiceProvider serviceProvider,
    string[] mcpEndpoints,
    string agentName,
    IEnumerable<string>? whitelistPatterns = null) : IAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey, string userId)
    {
        var chatClient = serviceProvider.GetRequiredService<IChatClient>();
        var approvalHandlerFactory = serviceProvider.GetRequiredService<IToolApprovalHandlerFactory>();
        var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

        var name = $"{agentName}-{agentKey.ChatId}-{agentKey.ThreadId}";

        var handler = approvalHandlerFactory.Create(agentKey);
        var effectiveClient = new ToolApprovalChatClient(chatClient, handler, whitelistPatterns);

        return new McpAgent(mcpEndpoints, effectiveClient, name, "", stateStore, userId);
    }
}