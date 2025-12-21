using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Files;

public class FileSearchTool(ISearchClient client, IStateManager stateManager)
{
    protected const string Name = "FileSearch";

    protected const string Description = """
                                         Search for a file in the internet using a multiple alternative search strings.
                                         The tool will perform one search per search string in the arguments and return
                                         the joint results for all the searches.
                                         Search strings should be concise and not include too many details.
                                         """;

    protected async Task<JsonNode> Run(string sessionId, string[] searchStrings, CancellationToken ct)
    {
        var results = await Task.WhenAll(searchStrings.Select(x => client.Search(x, ct)));
        var summarizedResults = results
            .SelectMany(x => x)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToArray();
        stateManager.SearchResults.Add(sessionId, summarizedResults);
        var output = summarizedResults
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Size,
                x.Seeders
            })
            .ToArray();

        return new JsonObject
        {
            ["status"] = "success",
            ["totalResults"] = output.Length,
            ["results"] = JsonSerializer.SerializeToNode(output)
        };
    }
}