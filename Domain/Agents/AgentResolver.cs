using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
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
    ILoggerFactory loggerFactory)
{
    private readonly Dictionary<int, List<Message>> _historics = [];
    private readonly Lock _lLock = new();

    public IAgent Resolve(AgentType agentType, int? sourceMessageId = null)
    {
        lock (_lLock)
        {
            return agentType switch
            {
                AgentType.Download => new DownloadAgent(
                    languageModel,
                    fileSearchTool,
                    fileDownloadTool,
                    waitForDownloadTool,
                    libraryDescriptionTool,
                    moveTool,
                    cleanupTool,
                    sourceMessageId is null ? [] : _historics.GetValueOrDefault(sourceMessageId.Value) ?? [],
                    loggerFactory.CreateLogger<DownloadAgent>()),
                _ => throw new ArgumentException($"Unknown agent type: {agentType}")
            };
        }
    }

    public void AssociateMessageIdToAgent(int messageId, IAgent agent)
    {
        lock (_lLock)
        {
            _historics[messageId] = agent.Messages;
        }
    }
}