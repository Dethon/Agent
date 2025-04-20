using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record LibraryDescriptionParams;

public class LibraryDescriptionTool(IFileSystemClient client, string libraryPath) : BaseTool, ITool
{
    public string Name => "LibraryDescription";

    private readonly JsonSerializerOptions _options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var result = client.DescribeDirectory(libraryPath);
        var jsonResult = JsonSerializer.SerializeToNode(result, _options);
        return jsonResult is not null
            ? Task.FromResult(jsonResult)
            : throw new InvalidOperationException("Failed to serialize LibraryDescriptionNode");
    }

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<LibraryDescriptionParams>
        {
            Name = Name,
            Description = """
                          Describes the library folder structure and its contents to be able to decide where to put 
                          downloaded files.
                          """
        };
    }
}