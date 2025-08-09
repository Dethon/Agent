using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class FileSearchTool(ISearchClient client, IStateManager stateManager) : BaseTool
{
    private const string Name = "FileSearch";

    private const string Description = """
                                       Search for a file in the internet using a search string. Search strings must be 
                                       concise and not include too many details.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context, 
        string searchString, 
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.SessionId ?? "";
            var results = await client.Search(searchString, cancellationToken);
            stateManager.SearchResults.Add(sessionId, results);

            return CreateResponse(new JsonObject
            {
                ["status"] = "success",
                ["message"] = "File search completed successfully",
                ["totalResults"] = results.Length,
                ["results"] = JsonSerializer.SerializeToNode(results)
            });
        }
        catch (Exception ex)
        {
            return CreateResponse(ex);
        }
    }
}