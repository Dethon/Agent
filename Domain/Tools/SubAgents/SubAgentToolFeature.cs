using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentToolFeature(
    SubAgentRegistryOptions registryOptions) : IDomainToolFeature
{
    private const string Feature = "subagents";

    public string FeatureName => Feature;

    public string? Prompt => SubAgentPrompt.SystemPrompt;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var runTool = new SubAgentRunTool(registryOptions, config);
        yield return AIFunctionFactory.Create(
            runTool.RunAsync,
            name: $"domain:{Feature}:{SubAgentRunTool.Name}",
            description: runTool.Description);
    }
}