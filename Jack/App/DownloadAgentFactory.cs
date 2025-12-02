using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;

namespace Jack.App;

using ResponseCallback = Func<AiResponse, CancellationToken, Task>;

public class DownloadAgentFactory(IChatClient chatClient, string[] mcpEndpoints) : IAgentFactory
{
    public Task<IAgent> Create(ResponseCallback responseCallback, CancellationToken ct)
    {
        return McpAgent.CreateAsync(mcpEndpoints, responseCallback, chatClient, ct);
    }
}