using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public class LibraryDescriptionTool(IFileSystemClient client, string libraryPath) : BaseTool, ITool
{
    public string Name => "LibraryDescription";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var result = await client.DescribeDirectory(libraryPath, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result);
        return jsonResult ?? throw new InvalidOperationException("Failed to serialize LibraryDescriptionNode");
    }

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition
        {
            Name = Name,
            Description = """
                          Describes the library folder structure to be able to decide where to put downloaded files.
                          The format is a JSON object with the following structure:
                          {
                              "path/to/folder/with/files": [
                                "filename1.ext",
                                "filename2.ext",
                                ...
                              ],
                              ...
                          }
                          """
        };
    }
}