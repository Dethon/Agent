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
public class McpFileDownloadTool(
    IDownloadClient client,
    IStateManager stateManager,
    DownloadPathConfig pathConfig,
    ILogger<McpFileDownloadTool> logger) : FileDownloadTool(client, stateManager, pathConfig)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        int searchResultId,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            return ToolResponse.Create(await Run(sessionId, searchResultId, cancellationToken));
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