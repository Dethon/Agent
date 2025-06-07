using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Attachments;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record FileSearchParams
{
    public required string SearchString { get; [UsedImplicitly] init; }
}

public record SearchResultToSerialize
{
    public string Title { [UsedImplicitly] get; init; }
    public string? Category { [UsedImplicitly] get; init; }
    public int Id { [UsedImplicitly] get; init; }
    public long? Size { [UsedImplicitly] get; init; }
    public long? Seeders { [UsedImplicitly] get; init; }
    public long? Peers { [UsedImplicitly] get; init; }

    public SearchResultToSerialize(SearchResult result)
    {
        Title = result.Title;
        Category = result.Category;
        Id = result.Id;
        Size = result.Size;
        Seeders = result.Seeders;
        Peers = result.Peers;
    }
}

public class FileSearchTool(
    ISearchClient client,
    SearchHistory history) : BaseTool<FileSearchTool, FileSearchParams>, IToolWithMetadata
{
    public static Type? ParamsType => typeof(FileSearchParams);
    public static string Name => "FileSearch";

    public static string Description => """
                                        Search for a file in the internet using a search string. Search strings must be concise and
                                        not include too many details.
                                        """;

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(parameters);

        var results = await client.Search(typedParams.SearchString, cancellationToken);
        history.Add(results);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File search completed successfully",
            ["totalResults"] = results.Length,
            ["results"] = JsonSerializer.SerializeToNode(results.Select(x => new SearchResultToSerialize(x)))
        };
    }
}