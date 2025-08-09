using Domain.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace Domain.Agents;

public class AgentResolver(
    DownloaderPrompt downloaderPrompt,
    ILargeLanguageModel languageModel,
    string[] mcpServerEndpoints,
    IMemoryCache cache)
{
    public async Task<IAgent> Resolve(
        int? threadId,
        Func<ChatResponse, CancellationToken, Task> writeMessageCallback,
        CancellationToken ct)
    {
        if (threadId is null)
        {
            return await CreateAgent(writeMessageCallback, ct);
        }

        var agent = await GetAgentFromCache(
            threadId.Value,
            () => CreateAgent(writeMessageCallback, ct));
        
        if (agent is null)
        {
            throw new InvalidOperationException($"Agent for thread {threadId} found in cache but was null.");
        }

        return agent;
    }

    private Task<IAgent> CreateAgent(
        Func<ChatResponse, CancellationToken, Task> writeMessageCallback, CancellationToken ct)
    {
        return Agent.CreateAsync(
            mcpServerEndpoints, downloaderPrompt.Get(), writeMessageCallback, languageModel, ct);
    }

    private async Task<IAgent?> GetAgentFromCache(int threadId, Func<Task<IAgent>> createAgent)
    {
        return await cache.GetOrCreateAsync($"IAgent-{threadId}", cacheEntry =>
        {
            cacheEntry.SetAbsoluteExpiration(DateTimeOffset.UtcNow.AddMonths(2));
            return createAgent();
        });
    }
}