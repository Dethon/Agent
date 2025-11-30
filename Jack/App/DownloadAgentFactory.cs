using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;

namespace Jack.App;

using ResponseCallback = Func<AiResponse, CancellationToken, Task>;

public class DownloadAgentFactory(
    OpenAiClient llmClient,
    string[] mcpEndpoints,
    IConversationHistoryStore conversationStore) : IAgentFactory
{
    public Task<IAgent> Create(string conversationId, ResponseCallback responseCallback, CancellationToken ct)
    {
        return McpAgent.CreateAsync(
            mcpEndpoints,
            conversationId,
            DownloaderPrompt.Get(),
            responseCallback,
            llmClient,
            conversationStore,
            ct);
    }
}