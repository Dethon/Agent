using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class FileSearchTool(ISearchClient client, IMemoryCache cache)
{
    private const string Name = "FileSearch";

    private const string Description = """
                                       Search for a file in the internet using a search string. Search strings must be concise and
                                       not include too many details.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<string> Run(string searchString, CancellationToken cancellationToken)
    {
        var results = await client.Search(searchString, cancellationToken);
        foreach (var result in results)
        {
            cache.Set(result.Id, result, DateTimeOffset.UtcNow.AddMonths(2));
        }
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File search completed successfully",
            ["totalResults"] = results.Length,
            ["results"] = JsonSerializer.SerializeToNode(results)
        }.ToJsonString();
    }
}