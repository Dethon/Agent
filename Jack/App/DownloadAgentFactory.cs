using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Infrastructure.LLMAdapters;

namespace Jack.App;

using ResponseCallback = Func<AiResponse, CancellationToken, Task>;

public class DownloadAgentFactory(OpenAiClient llmClient, string[] mcpEndpoints) : IAgentFactory
{
    public Task<IAgent> Create(ResponseCallback responseCallback, CancellationToken ct)
    {
        return Agent.CreateAsync(mcpEndpoints, DownloaderPrompt.Get(), responseCallback, llmClient, ct);
    }
}