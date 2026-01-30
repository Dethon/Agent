using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Downloads;
using Infrastructure.Utils;
using Infrastructure.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpGetDownloadStatusTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager) : GetDownloadStatusTool(client, searchResultsManager)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        int downloadId,
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;
        return ToolResponse.Create(await Run(sessionId, downloadId, cancellationToken));
    }
}
