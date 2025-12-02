using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;

namespace Jack.App;

using ResponseCallback = Func<AiResponse, CancellationToken, Task>;

public class DownloadAgentFactory(OpenAiClient llmClient, string[] mcpEndpoints) : IAgentFactory
{
    public Task<IAgent> Create(ResponseCallback responseCallback, CancellationToken ct)
    {
        return McpAgent.CreateAsync(mcpEndpoints, responseCallback, llmClient, ct);
    }
}