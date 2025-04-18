using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public record FileSearchParams
{
    public required string SearchString { get; init; }
}

public abstract class FileSearchTool : ITool
{
    public string Name => "FileSearch";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = parameters?.Deserialize<FileSearchParams>();
        if (typedParams is null)
            throw new ArgumentNullException(
                nameof(parameters), $"{typeof(FileSearchTool)} cannot have null parameters");

        return await Resolve(typedParams, cancellationToken);
    }

    protected abstract Task<JsonNode> Resolve(FileSearchParams parameters, CancellationToken cancellationToken);

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileSearchParams>
        {
            Name = Name,
            Description = "Search for file in the internet using a search string",
            Parameters = new FileSearchParams
            {
                SearchString = string.Empty
            }
        };
    }
}