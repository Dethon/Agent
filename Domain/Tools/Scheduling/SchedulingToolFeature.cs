using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.Scheduling;

public class SchedulingToolFeature(
    ScheduleCreateTool createTool,
    ScheduleListTool listTool,
    ScheduleDeleteTool deleteTool) : IDomainToolFeature
{
    private const string Feature = "scheduling";

    public string FeatureName => Feature;

    public IEnumerable<AIFunction> GetTools()
    {
        yield return AIFunctionFactory.Create(
            createTool.RunAsync,
            name: $"domain:{Feature}:{ScheduleCreateTool.Name}");

        yield return AIFunctionFactory.Create(
            listTool.RunAsync,
            name: $"domain:{Feature}:{ScheduleListTool.Name}");

        yield return AIFunctionFactory.Create(
            deleteTool.RunAsync,
            name: $"domain:{Feature}:{ScheduleDeleteTool.Name}");
    }
}