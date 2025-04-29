using Domain.Contracts;
using Domain.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Domain.Agents;

public class AgentResolver(
    ILargeLanguageModel languageModel,
    FileDownloadTool fileDownloadTool,
    FileSearchTool fileSearchTool,
    WaitForDownloadTool waitForDownloadTool,
    MoveTool moveTool,
    CleanupTool cleanupTool,
    LibraryDescriptionTool libraryDescriptionTool,
    IMemoryCache cache,
    ILoggerFactory loggerFactory)
{
    public IAgent Resolve(AgentType agentType, int? sourceMessageId = null)
    {
        return GetAgentFromCache(sourceMessageId) ?? agentType switch
        {
            AgentType.Download => new DownloadAgent(
                languageModel,
                fileSearchTool,
                fileDownloadTool,
                waitForDownloadTool,
                libraryDescriptionTool,
                moveTool,
                cleanupTool,
                loggerFactory.CreateLogger<DownloadAgent>()),
            _ => throw new ArgumentException($"Unknown agent type: {agentType}")
        };
    }

    public void AssociateMessageToAgent(int messageId, IAgent agent)
    {
        cache.Set($"IAgent{messageId}", agent, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddDays(1)
        });
    }

    private IAgent? GetAgentFromCache(int? sourceMessageId)
    {
        if (sourceMessageId.HasValue && cache.TryGetValue($"IAgent{sourceMessageId}", out IAgent? agent))
        {
            return agent;
        }

        return null;
    }
}