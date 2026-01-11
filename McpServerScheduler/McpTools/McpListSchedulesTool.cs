using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Scheduling;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduler.McpTools;

[McpServerToolType]
public class McpListSchedulesTool(
    IScheduler scheduler,
    ILogger<McpListSchedulesTool> logger)
    : ListSchedulesTool(scheduler)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("User identifier for scoping the schedules")]
        string userId,
        [Description("Filter by status: active, paused, completed, failed, expired, cancelled")]
        string? status = null,
        [Description("Filter by tag")]
        string? tag = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Run(userId, status, tag, cancellationToken);
            return ToolResponse.Create(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName}", Name);
            return ToolResponse.Create(ex);
        }
    }
}
