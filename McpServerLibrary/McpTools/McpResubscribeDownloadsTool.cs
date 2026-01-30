using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Downloads;
using Infrastructure.Utils;
using Infrastructure.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpResubscribeDownloadsTool(
    IDownloadClient downloadClient,
    ITrackedDownloadsManager trackedDownloadsManager)
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
}
