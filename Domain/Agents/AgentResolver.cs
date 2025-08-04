using Domain.Contracts;
using Domain.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Domain.Agents;

public class AgentResolver(
    DownloaderPrompt downloaderPrompt,
    ILargeLanguageModel languageModel,
    FileDownloadTool fileDownloadTool,
    FileSearchTool fileSearchTool,
    GetDownloadStatusTool getDownloadStatusTool,
    MoveTool moveTool,
    CleanupTool cleanupTool,
    ListDirectoriesTool listDirectoriesTool,
    ListFilesTool listFilesTool,
    IMemoryCache cache,
    ILoggerFactory loggerFactory) : IAgentResolver
{
    public async Task<IAgent> Resolve(AgentType agentType, int? threadId = null)
    {
        if (threadId is null)
        {
            return await AgentFactory(agentType);
        }

        var agent = await GetAgentFromCache(agentType, threadId.Value, () => AgentFactory(agentType));
        if (agent is null)
        {
            throw new InvalidOperationException($"{agentType} for thread {threadId} found in cache but was null.");
        }
        
        return agent;
    }

    private async Task<IAgent> AgentFactory(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Download => new Agent(
                messages: await downloaderPrompt.Get(null),
                llm: languageModel,
                tools:
                [
                    fileSearchTool,
                    fileDownloadTool,
                    getDownloadStatusTool,
                    listDirectoriesTool,
                    listFilesTool,
                    moveTool,
                    cleanupTool
                ],
                maxDepth: 10,
                logger: loggerFactory.CreateLogger<Agent>()),
            _ => throw new ArgumentException($"Unknown agent type: {agentType}")
        };
    }

    private async Task<IAgent?> GetAgentFromCache(AgentType agentType, int threadId, Func<Task<IAgent>> createAgent)
    {
        return await cache.GetOrCreateAsync($"IAgent-{agentType}-{threadId}", cacheEntry =>
        {
            cacheEntry.SetAbsoluteExpiration(DateTimeOffset.UtcNow.AddMonths(2));
            return createAgent();
        });
    }
}