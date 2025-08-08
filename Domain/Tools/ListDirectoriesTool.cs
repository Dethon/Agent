using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.Tools.Config;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class ListDirectoriesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    private const string Name = "ListDirectories";

    private const string Description = """
                                       Lists all directories in the library. It only returns directories, not files.
                                       Must be used to explore the library and find the correct place into which 
                                       downloaded files should be stored.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<string> Run(CancellationToken cancellationToken)
    {
        var result = await client.ListDirectoriesIn(libraryPath.BaseLibraryPath, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result) ?? 
                         throw new InvalidOperationException("Failed to serialize ListDirectories");
        return jsonResult.ToJsonString();
    }
}