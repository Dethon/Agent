using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Download.McpTools;

[McpServerToolType]
public class McpFileSearchTool(ISearchClient client, IStateManager stateManager) :
    FileSearchTool(client, stateManager)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        string searchString,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.SessionId ?? "";
            return ToolResponse.Create(await Run(sessionId, searchString, cancellationToken));
        }
        catch (Exception ex)
        {
            return ToolResponse.Create(ex);
        }
    }
}