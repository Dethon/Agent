using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters;

namespace Jack.App;

using ResponseCallback = Func<AiResponse, CancellationToken, Task>;

public class DownloaderAgentFactory(OpenAiClient llmClient, string[] mcpEndpoints) : IAgentFactory
{
    public Task<IAgent> Create(ResponseCallback responseCallback, CancellationToken cancellationToken)
    {
        var systemPrompt = DownloaderPrompt.Get().Select(x => x.ToChatMessage()).ToArray();
        return Agent.CreateAsync(mcpEndpoints, systemPrompt, responseCallback, llmClient, cancellationToken);
    }
}