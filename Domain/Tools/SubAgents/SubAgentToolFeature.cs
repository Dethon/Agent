using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentToolFeature(
    SubAgentRegistryOptions registryOptions) : IDomainToolFeature
{
    private const string Feature = "subagents";

    public string FeatureName => Feature;

    public IEnumerable<AIFunction> GetTools() =>
        throw new InvalidOperationException("SubAgentToolFeature requires FeatureConfig. Use GetTools(FeatureConfig) instead.");

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var runTool = new SubAgentRunTool(registryOptions, config);
        yield return AIFunctionFactory.Create(
            runTool.RunAsync,
            name: $"domain:{Feature}:{SubAgentRunTool.Name}",
            description: runTool.Description);
    }
}
