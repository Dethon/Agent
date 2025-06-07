using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools;

public class ListDirectoriesTool(
    IFileSystemClient client,
    string libraryPath) : BaseTool<ListDirectoriesTool>, IToolWithMetadata
{
    public static Type? ParamsType => null; // No parameters needed for this tool
    public static string Name => "ListDirectories";

    public static string Description => """
                                        Lists all directories in the library. It only returns directories, not files.
                                        Must be used to explore the library and find the correct place into which 
                                        downloaded files should be stored.
                                        """;

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var result = await client.ListDirectoriesIn(libraryPath, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result);
        return jsonResult ?? throw new InvalidOperationException("Failed to serialize ListDirectories");
    }
}