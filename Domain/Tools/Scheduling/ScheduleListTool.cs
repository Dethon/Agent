using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling;

public class ScheduleListTool(IScheduleStore store)
{
    public const string Name = "schedule_list";

    public const string Description = """
        Lists all scheduled agent tasks. Shows schedule ID, agent name, prompt preview,
        schedule timing (cron or one-shot), next run time, and target channel.
        """;

    [Description(Description)]
    public async Task<JsonNode> RunAsync(CancellationToken ct = default)
    {
        var schedules = await store.ListAsync(ct);

        var summaries = schedules
            .Select(s => new ScheduleSummary(
                s.Id,
                s.Agent.Name,
                TruncatePrompt(s.Prompt),
                s.CronExpression,
                s.RunAt,
                s.NextRunAt,
                s.Target.Channel))
            .ToList();

        return new JsonObject
        {
            ["count"] = summaries.Count,
            ["schedules"] = new JsonArray(summaries.Select(ToJson).ToArray())
        };
    }

    private static string TruncatePrompt(string prompt)
    {
        const int maxLength = 100;
        return prompt.Length <= maxLength ? prompt : $"{prompt[..maxLength]}...";
    }

    private static JsonNode ToJson(ScheduleSummary summary)
    {
        var node = new JsonObject
        {
            ["id"] = summary.Id,
            ["agentName"] = summary.AgentName,
            ["prompt"] = summary.Prompt,
            ["channel"] = summary.Channel
        };

        if (summary.CronExpression is not null)
        {
            node["cronExpression"] = summary.CronExpression;
        }

        if (summary.RunAt.HasValue)
        {
            node["runAt"] = summary.RunAt.Value.ToString("O");
        }

        if (summary.NextRunAt.HasValue)
        {
            node["nextRunAt"] = summary.NextRunAt.Value.ToString("O");
        }

        return node;
    }
}
