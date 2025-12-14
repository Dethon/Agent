using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Infrastructure.Utils;
using McpServerLibrary.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpCleanupDownloadTool(
    IDownloadClient downloadClient,
    IStateManager stateManager,
    IFileSystemClient fileSystemClient,
    DownloadPathConfig downloadPath,
    ILogger<McpCleanupDownloadTool> logger)
    : CleanupDownloadTool(downloadClient, stateManager, fileSystemClient, downloadPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        int downloadId,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            var result = await Run(sessionId, downloadId, cancellationToken);
            await context.Server.SendNotificationAsync(
                "notifications/resources/list_changed",
                cancellationToken: cancellationToken);
            return ToolResponse.Create(result);
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