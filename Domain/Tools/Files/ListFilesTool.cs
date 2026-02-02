using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class ListFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "ListFiles";

    protected const string Description = """
                                         Lists all files in the specified directory. Returns only files, not directories.
                                         The path must be absolute and derived from the ListDirectories tool.
                                         """;

    protected async Task<JsonNode> Run(string path, CancellationToken cancellationToken)
    {
        if (!path.StartsWith(libraryPath.BaseLibraryPath))
        {
            throw new InvalidOperationException($"""
                                                 {typeof(ListFilesTool)} parameter must be absolute paths derived from 
                                                 the ListDirectories tool response. 
                                                 They must start with the library path: {libraryPath}
                                                 """);
        }

        var result = await client.ListFilesIn(path, cancellationToken);
        return JsonSerializer.SerializeToNode(result) ??
               throw new InvalidOperationException("Failed to serialize ListFiles");
    }
}