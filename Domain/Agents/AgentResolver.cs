using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Attachments;

namespace Domain.Agents;

public class AgentResolver(
    ILargeLanguageModel languageModel,
    FileDownloadTool fileDownloadTool,
    FileSearchTool fileSearchTool,
    FileMoveTool fileMoveTool,
    LibraryDescriptionTool libraryDescriptionTool,
    DownloadMonitor downloadMonitor)
{
    public IAgent Resolve(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Download => new DownloadAgent(
                languageModel,
                fileSearchTool,
                fileDownloadTool,
                libraryDescriptionTool,
                fileMoveTool,
                downloadMonitor),
            _ => throw new ArgumentException($"Unknown agent type: {agentType}")
        };
    }
}