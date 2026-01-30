using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Files;
using Infrastructure.Utils;
using Infrastructure.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpFileSearchTool(
    ISearchClient client,
    ISearchResultsManager searchResultsManager) : FileSearchTool(client, searchResultsManager)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        string[] searchStrings,
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;
        return ToolResponse.Create(await Run(sessionId, searchStrings, cancellationToken));
    }
}
