using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class ListDirectoriesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "ListDirectories";

    protected const string Description = """
                                         Lists all directories as a tree of absolute paths. Returns only directories, not files.
                                         Call once per conversation and reuse the result—directory structure rarely changes.
                                         """;

    protected async Task<JsonNode> Run(CancellationToken cancellationToken)
    {
        var result = await client.ListDirectoriesIn(libraryPath.BaseLibraryPath, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result);
        return jsonResult ?? throw new InvalidOperationException("Failed to retrieve list of directories");
    }
}