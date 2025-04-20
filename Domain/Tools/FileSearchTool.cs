using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Attachments;
using JetBrains.Annotations;

namespace Domain.Tools;

public record FileSearchParams
{
    public required string SearchString { get; init; }
}

public record SearchResult
{
    public required string Title { get; init; }
    public string? Category { get; init; }
    public required int Id { get; init; }
    public long? Size { get; init; }
    public long? Seeders { get; init; }
    public long? Peers { get; init; }
    [JsonIgnore] public required string Link { get; init; }
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

public abstract class FileSearchTool(SearchHistory history) : ITool
{
    public string Name => "FileSearch";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = parameters?.Deserialize<FileSearchParams>();
        if (typedParams is null)
        {
            throw new ArgumentNullException(
                nameof(parameters), $"{typeof(FileSearchTool)} cannot have null parameters");
        }

        var results = await Resolve(typedParams, cancellationToken);
        history.Add(results);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File search completed successfully",
            ["totalResults"] = results.Length,
            ["results"] = JsonSerializer.SerializeToNode(results.Select(x => new SearchResultToSerialize(x)))
        };
    }

    protected abstract Task<SearchResult[]> Resolve(FileSearchParams parameters, CancellationToken cancellationToken);

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileSearchParams>
        {
            Name = Name,
            Description = """
                          Search for a file in the internet using a search string. Search strings must be concise and
                          not include too many details.
                          """
        };
    }
}