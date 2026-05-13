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
        [Description("Agent ID to execute the task")]
        string agentId,
        [Description("The prompt/task to execute")]
        string prompt,
        [Description("Cron expression for recurring schedules (5-field format)")]
        string? cronExpression = null,
        [Description("ISO 8601 datetime for one-time execution (UTC)")]
        DateTime? runAt = null,
        [Description("Optional user context for the prompt")]
        string? userId = null,
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
            var available = agentProvider.GetAll(userId);
            var availableIds = available.Count == 0
                ? "none registered"
                : string.Join(", ", available.Select(a => $"'{a.Id}'"));
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"Agent '{agentId}' not found. Available agents: {availableIds}",
                retryable: false);
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
            return ToolError.Create(ToolError.Codes.InvalidArgument, "agentId is required", retryable: false);
        }

        if (cronExpression is null && runAt is null)
        {
            return ToolError.Create(ToolError.Codes.InvalidArgument, "Either cronExpression or runAt must be provided", retryable: false);
        }

        if (cronExpression is not null && runAt is not null)
        {
            return ToolError.Create(ToolError.Codes.InvalidArgument, "Provide only cronExpression OR runAt, not both", retryable: false);
        }

        if (cronExpression is not null && !cronValidator.IsValid(cronExpression))
        {
            return ToolError.Create(ToolError.Codes.InvalidArgument, $"Invalid cron expression: {cronExpression}", retryable: false);
        }

        if (runAt is not null && runAt <= DateTime.UtcNow)
        {
            return ToolError.Create(ToolError.Codes.InvalidArgument, "runAt must be in the future", retryable: false);
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