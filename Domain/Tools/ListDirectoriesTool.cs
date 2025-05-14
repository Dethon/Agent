using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public class ListDirectoriesTool(IFileSystemClient client, string libraryPath) : BaseTool, ITool
{
    public string Name => "ListDirectories";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var result = await client.ListDirectoriesIn(libraryPath, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result);
        return jsonResult ?? throw new InvalidOperationException("Failed to serialize ListDirectories");
    }

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition
        {
            Name = Name,
            Description = """
                          Lists all directories in the library. It only returns directories, not files.
                          Must be used to explore the library and find the correct place into which downloaded files 
                          should be stored.
                          """
        };
    }
}