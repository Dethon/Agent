using Domain.Contracts;
using Domain.Tools;

namespace Domain.Agents;

public class AgentResolver(
    ILargeLanguageModel languageModel,
    FileDownloadTool fileDownloadTool,
    FileSearchTool fileSearchTool)
{
    public IAgent Resolve(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Download => new DownloadAgent(languageModel, fileSearchTool, fileDownloadTool),
            _ => throw new ArgumentException($"Unknown agent type: {agentType}")
        };
    }
}