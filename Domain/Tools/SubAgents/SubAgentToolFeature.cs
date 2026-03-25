using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentToolFeature(SubAgentRunTool runTool) : IDomainToolFeature
{
    private const string Feature = "subagents";

    public string FeatureName => Feature;

    public IEnumerable<AIFunction> GetTools()
    {
        yield return AIFunctionFactory.Create(
            runTool.RunAsync,
            name: $"domain:{Feature}:{SubAgentRunTool.Name}",
            description: runTool.Description);
    }
}
