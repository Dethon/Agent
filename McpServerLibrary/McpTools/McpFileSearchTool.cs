using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Utils;
using McpServerLibrary.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpFileSearchTool(
    ISearchClient client,
    IStateManager stateManager,
    ILogger<McpFileSearchTool> logger) : FileSearchTool(client, stateManager)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        string[] searchStrings,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            return ToolResponse.Create(await Run(sessionId, searchStrings, cancellationToken));
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