using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class RemoveFileTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "RemoveFile";

    protected const string Description = """
                                         Removes a file from the library.
                                         The path must be an absolute path derived from the ListFiles tool response.
                                         It must start with the library path.
                                         """;

    protected async Task<JsonNode> Run(string path, CancellationToken cancellationToken)
    {
        if (!path.StartsWith(libraryPath.BaseLibraryPath))
        {
            throw new InvalidOperationException($"""
                                                 {typeof(RemoveFileTool)} parameter must be an absolute path derived from
                                                 the ListFiles tool response.
                                                 It must start with the library path: {libraryPath}
                                                 """);
        }

        await client.RemoveFile(path, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File removed successfully",
            ["path"] = path
        };
    }
}