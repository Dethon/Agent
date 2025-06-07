using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools;

public class LibraryDescriptionTool(
    IFileSystemClient client,
    string libraryPath) : BaseTool<LibraryDescriptionTool, null >, ITool
{
public static string Name => "LibraryDescription";

public static string Description => """
                                    Describes the library folder structure to be able to decide where to put downloaded files.
                                    Pay attention to the paths in the result.
                                    The format is a JSON object with the following structure:
                                    {
                                        "path/to/folder/with/files": [
                                          "filename1.ext",
                                          "filename2.ext",
                                          ...
                                        ],
                                        ...
                                    }
                                    """;

public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
{
    var result = await client.DescribeDirectory(libraryPath, cancellationToken);
    var jsonResult = JsonSerializer.SerializeToNode(result);
    return jsonResult ?? throw new InvalidOperationException("Failed to serialize LibraryDescriptionNode");
}
}