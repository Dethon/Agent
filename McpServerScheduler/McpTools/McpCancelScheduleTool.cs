using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Scheduling;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduler.McpTools;

[McpServerToolType]
public class McpCancelScheduleTool(
    IScheduler scheduler,
    ILogger<McpCancelScheduleTool> logger)
    : CancelScheduleTool(scheduler)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("User identifier for scoping the schedule")]
        string userId,
        [Description("The task ID to cancel")]
        string taskId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Run(userId, taskId, cancellationToken);
            return ToolResponse.Create(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName}", Name);
            return ToolResponse.Create(ex);
        }
    }
}
