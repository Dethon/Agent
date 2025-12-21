using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class ListDirectoriesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "ListDirectories";

    protected const string Description = """
                                         Lists all directories in the library. It only returns directories, not files.
                                         Must be used to explore the library and find the place in which downloaded
                                         files are currently located and where they should be stored.
                                         This tool returns a list of absolute directories and subdirectories in the 
                                         library.
                                         IMPORTANT: The directory structure rarely changes. Call this tool only once 
                                         per conversation and reuse the result for subsequent operations.
                                         """;

    protected async Task<JsonNode> Run(CancellationToken cancellationToken)
    {
        var result = await client.ListDirectoriesIn(libraryPath.BaseLibraryPath, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result);
        return jsonResult ?? throw new InvalidOperationException("Failed to retrieve list of directories");
    }
}