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
            name: $"domain__{Feature}__{SubAgentRunTool.Name}",
            description: runTool.Description);

        var checkTool = new SubAgentCheckTool(config);
        yield return AIFunctionFactory.Create(
            checkTool.RunAsync,
            name: $"domain__{Feature}__{SubAgentCheckTool.Name}",
            description: SubAgentCheckTool.Description);

        var cancelTool = new SubAgentCancelTool(config);
        yield return AIFunctionFactory.Create(
            cancelTool.RunAsync,
            name: $"domain__{Feature}__{SubAgentCancelTool.Name}",
            description: SubAgentCancelTool.Description);

        var waitTool = new SubAgentWaitTool(config);
        yield return AIFunctionFactory.Create(
            waitTool.RunAsync,
            name: $"domain__{Feature}__{SubAgentWaitTool.Name}",
            description: SubAgentWaitTool.Description);

        var listTool = new SubAgentListTool(config);
        yield return AIFunctionFactory.Create(
            listTool.RunAsync,
            name: $"domain__{Feature}__{SubAgentListTool.Name}",
            description: SubAgentListTool.Description);

        var releaseTool = new SubAgentReleaseTool(config);
        yield return AIFunctionFactory.Create(
            releaseTool.RunAsync,
            name: $"domain__{Feature}__{SubAgentReleaseTool.Name}",
            description: SubAgentReleaseTool.Description);
    }
}