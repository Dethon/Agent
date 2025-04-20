using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public record FileMoveParams
{
    public required string SourceFile { get; init; }
    public required string DestinationPath { get; init; }
}

public abstract class FileMoveTool : ITool
{
    public string Name => "FileMove";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = parameters?.Deserialize<FileMoveParams>();
        if (typedParams is null)
        {
            throw new ArgumentNullException(
                nameof(parameters), $"{typeof(FileMoveTool)} cannot have null parameters");
        }

        return await Resolve(typedParams, cancellationToken);
    }

    protected abstract Task<JsonNode> Resolve(FileMoveParams parameters, CancellationToken cancellationToken);

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileMoveParams>
        {
            Name = Name,
            Description = """
                          Moves a file to a destination folder. Both arguments have to be absolute paths. 
                          If the destination folder does not exist it will be created."
                          """
        };
    }
}