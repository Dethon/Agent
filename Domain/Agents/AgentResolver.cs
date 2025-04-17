using Domain.Contracts;

namespace Domain.Agents;

public static class AgentResolver
{
    public static IAgent Resolve(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Download => new DownloadAgent(),
            _ => throw new ArgumentException($"Unknown agent type: {agentType}")
        };
    }
}
