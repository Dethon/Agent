using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools;

public class FileSearchTool(ISearchClient client, IStateManager stateManager)
{
    protected const string Name = "FileSearch";

    protected const string Description = """
                                         Search for a file in the internet using a search string. Search strings must be 
                                         concise and not include too many details.
                                         """;

    public async Task<JsonNode> Run(string sessionId, string searchString, CancellationToken ct)
    {
        var results = await client.Search(searchString, ct);
        stateManager.SearchResults.Add(sessionId, results);

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File search completed successfully",
            ["totalResults"] = results.Length,
            ["results"] = JsonSerializer.SerializeToNode(results)
        };
    }
}