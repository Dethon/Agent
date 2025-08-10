using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools;

public class ListDirectoriesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "ListDirectories";

    protected const string Description = """
                                         Lists all directories in the library. It only returns directories, not files.
                                         Must be used to explore the library and find the correct place into which 
                                         downloaded files should be stored.
                                         """;

    public async Task<JsonNode> Run(CancellationToken cancellationToken)
    {
        var result = await client.ListDirectoriesIn(libraryPath.BaseLibraryPath, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result);
        return jsonResult ?? throw new InvalidOperationException("Failed to retrieve list of directories");
    }
}