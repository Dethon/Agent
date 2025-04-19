using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public abstract class LibraryDescriptionTool : ITool
{
    public string Name => "LibraryDescription";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        return await Resolve(cancellationToken);
    }

    protected abstract Task<JsonNode> Resolve(CancellationToken cancellationToken);

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileSearchParams>
        {
            Name = Name,
            Description = """
                          Describes the library folder structure and its contents to be able to decide where to put 
                          downloaded files.
                          """
        };
    }
}