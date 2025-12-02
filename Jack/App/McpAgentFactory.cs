using Domain.Agents;
using Domain.DTOs;
using Domain.Monitor;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;

namespace Jack.App;

public class McpAgentFactory(IChatClient chatClient, string[] mcpEndpoints, string name, string description)
    : IMcpAgentFactory
{
    public async Task<CancellableAiAgent> Create(
        Func<AiResponse, CancellationToken, Task> responseCallback,
        CancellationToken ct)
    {
        return await McpAgent.CreateAsync(mcpEndpoints, responseCallback, chatClient, name, description, ct);
    }
}