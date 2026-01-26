using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling;

public class ScheduleCreateTool(
    IScheduleStore store,
    ICronValidator cronValidator,
    IAgentDefinitionProvider agentProvider)
{
    public const string Name = "schedule_create";

    public const string Description = """
                                      Creates a scheduled agent task. The specified agent will run with the given prompt
                                      at the scheduled time(s).

                                      For recurring schedules, use cronExpression (standard 5-field cron format):
                                      - "0 9 * * *" = every day at 9:00 AM
                                      - "0 */2 * * *" = every 2 hours
                                      - "30 14 * * 1-5" = weekdays at 2:30 PM

                                      For one-time schedules, use runAt with a UTC datetime.

                                      Results are delivered to WebChat when available.
                                      """;

    [Description(Description)]
    public async Task<JsonNode> RunAsync(
        [Description("Agent ID to execute the task")] string agentId,
        [Description("The prompt/task to execute")] string prompt,
        [Description("Cron expression for recurring schedules (5-field format)")] string? cronExpression,
        [Description("ISO 8601 datetime for one-time execution (UTC)")] DateTime? runAt,
        [Description("Optional user context for the prompt")] string? userId,
        CancellationToken ct = default)
    {
        var validationError = Validate(agentId, cronExpression, runAt);
        if (validationError is not null)
        {
            return validationError;
        }

        var agentDefinition = agentProvider.GetById(agentId);
        if (agentDefinition is null)
        {
            return new JsonObject { ["error"] = $"Agent '{agentId}' not found" };
        }

        var nextRunAt = CalculateNextRunAt(cronExpression, runAt);

        var schedule = new Schedule
        {
            Id = $"sched_{Guid.NewGuid():N}",
            Agent = agentDefinition,
            Prompt = prompt,
            CronExpression = cronExpression,
            RunAt = runAt,
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            NextRunAt = nextRunAt
        };

        await store.CreateAsync(schedule, ct);

        return new JsonObject
        {
            ["status"] = "created",
            ["scheduleId"] = schedule.Id,
            ["agentName"] = agentDefinition.Name,
            ["nextRunAt"] = nextRunAt?.ToString("O")
        };
    }

    private JsonObject? Validate(string agentId, string? cronExpression, DateTime? runAt)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return new JsonObject { ["error"] = "agentId is required" };
        }

        if (cronExpression is null && runAt is null)
        {
            return new JsonObject { ["error"] = "Either cronExpression or runAt must be provided" };
        }

        if (cronExpression is not null && runAt is not null)
        {
            return new JsonObject { ["error"] = "Provide only cronExpression OR runAt, not both" };
        }

        if (cronExpression is not null && !cronValidator.IsValid(cronExpression))
        {
            return new JsonObject { ["error"] = $"Invalid cron expression: {cronExpression}" };
        }

        if (runAt is not null && runAt <= DateTime.UtcNow)
        {
            return new JsonObject { ["error"] = "runAt must be in the future" };
        }

        return null;
    }

    private DateTime? CalculateNextRunAt(string? cronExpression, DateTime? runAt)
    {
        if (runAt.HasValue)
        {
            return runAt.Value;
        }

        if (cronExpression is not null)
        {
            return cronValidator.GetNextOccurrence(cronExpression, DateTime.UtcNow);
        }

        return null;
    }
}
