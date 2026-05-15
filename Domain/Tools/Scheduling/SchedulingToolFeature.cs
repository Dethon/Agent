using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.Scheduling;

public class SchedulingToolFeature(
    ScheduleCreateTool createTool,
    ScheduleListTool listTool,
    ScheduleDeleteTool deleteTool,
    IAgentDefinitionProvider agentProvider) : IDomainToolFeature
{
    private const string Feature = "scheduling";

    public string FeatureName => Feature;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        yield return AIFunctionFactory.Create(
            createTool.RunAsync,
            name: $"domain__{Feature}__{ScheduleCreateTool.Name}",
            description: BuildCreateDescription(config.UserId));

        yield return AIFunctionFactory.Create(
            listTool.RunAsync,
            name: $"domain__{Feature}__{ScheduleListTool.Name}");

        yield return AIFunctionFactory.Create(
            deleteTool.RunAsync,
            name: $"domain__{Feature}__{ScheduleDeleteTool.Name}");
    }

    private string BuildCreateDescription(string? userId)
    {
        var agents = agentProvider.GetAll(userId);
        var agentList = agents.Count == 0
            ? "(no agents are currently registered)"
            : string.Join("\n", agents.Select(a =>
                $"- \"{a.Id}\": {a.Description ?? a.Name}"));

        return $"""
                {ScheduleCreateTool.Description}

                Available agents (use the quoted id as agentId):
                {agentList}
                """;
    }
}