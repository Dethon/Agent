using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Downloads;
using Infrastructure.Utils;
using Infrastructure.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpResubscribeDownloadsTool(
    IDownloadClient downloadClient,
    ITrackedDownloadsManager trackedDownloadsManager,
    ILogger<McpResubscribeDownloadsTool> logger)
    : ResubscribeDownloadsTool(downloadClient, trackedDownloadsManager)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        [Description("Array of download IDs to resubscribe to")]
        int[] downloadIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            var result = await Run(sessionId, downloadIds, cancellationToken);

            if (result.HasNewSubscriptions)
            {
                await context.Server.SendNotificationAsync(
                    "notifications/resources/list_changed",
                    cancellationToken: cancellationToken);
            }

            return ToolResponse.Create(result.Response);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "Error in {ToolName} tool", Name);
            }

            return ToolResponse.Create(ex);
        }
    }
}