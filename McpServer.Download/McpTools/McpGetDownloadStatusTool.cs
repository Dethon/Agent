using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Download.McpTools;

[McpServerToolType]
public class McpGetDownloadStatusTool(IDownloadClient client, IStateManager stateManager) :
    GetDownloadStatusTool(client, stateManager)
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
            return ToolResponse.Create(ex);
        }
    }
}