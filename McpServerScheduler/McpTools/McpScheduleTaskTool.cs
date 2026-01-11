using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Scheduling;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduler.McpTools;

[McpServerToolType]
public class McpScheduleTaskTool(
    IScheduler scheduler,
    ILogger<McpScheduleTaskTool> logger)
    : ScheduleTaskTool(scheduler)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("User identifier for scoping the schedule")]
        string userId,
        [Description("Chat ID where notifications will be sent")]
        long chatId,
        [Description("Short name for the task")]
        string name,
        [Description("Description of what the task does")]
        string description,
        [Description("The command/instruction to execute")]
        string command,
        [Description("Cron expression (e.g., '0 9 * * *' for daily at 9am)")]
        string schedule,
        [Description("Thread ID for notifications (optional)")]
        long? threadId = null,
        [Description("Comma-separated tags for categorization")]
        string? tags = null,
        [Description("Maximum number of executions (omit for unlimited)")]
        int? maxRuns = null,
        [Description("ISO 8601 datetime when task expires")]
        string? expiresAt = null,
        [Description("Policy for missed executions: skip_to_next, run_immediately, run_once_if_missed")]
        string? missedPolicy = null,
        [Description("ISO 8601 datetime for one-time execution (instead of cron)")]
        string? runOnce = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Run(
                userId, chatId, name, description, command, schedule,
                threadId, tags, maxRuns, expiresAt, missedPolicy, runOnce,
                cancellationToken);
            return ToolResponse.Create(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName}", Name);
            return ToolResponse.Create(ex);
        }
    }
}
