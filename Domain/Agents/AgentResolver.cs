using Domain.Contracts;

namespace Domain.Agents;

public class AgentResolver(ILargeLanguageModel languageModel)
{
    public IAgent Resolve(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Download => new DownloadAgent(languageModel),
            _ => throw new ArgumentException($"Unknown agent type: {agentType}")
        };
    }
}