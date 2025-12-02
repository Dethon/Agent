using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpGetDownloadStatusTool(
    IDownloadClient client,
    IStateManager stateManager,
    ILogger<McpGetDownloadStatusTool> logger) : GetDownloadStatusTool(client, stateManager)
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
            var sessionId = context.Server.SessionId ?? "";
            return ToolResponse.Create(await Run(sessionId, downloadId, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName} tool", Name);
            return ToolResponse.Create(ex);
        }
    }
}