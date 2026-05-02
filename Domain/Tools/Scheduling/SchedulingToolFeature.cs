using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.Scheduling;

public class SchedulingToolFeature(
    ScheduleCreateTool createTool,
    ScheduleListTool listTool,
    ScheduleDeleteTool deleteTool) : IDomainToolFeature
{
    private const string Feature = "scheduling";

    public string FeatureName => Feature;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        yield return AIFunctionFactory.Create(
            createTool.RunAsync,
            name: $"domain__{Feature}__{ScheduleCreateTool.Name}");

        yield return AIFunctionFactory.Create(
            listTool.RunAsync,
            name: $"domain__{Feature}__{ScheduleListTool.Name}");

        yield return AIFunctionFactory.Create(
            deleteTool.RunAsync,
            name: $"domain__{Feature}__{ScheduleDeleteTool.Name}");
    }
}