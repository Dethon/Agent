using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class RemoveTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "Remove";

    protected const string Description = """
                                         Removes a file or directory by moving it to a trash folder.
                                         The path can be absolute (under the library root) or relative
                                         (resolved against the library root).
                                         """;

    protected async Task<JsonNode> Run(string path, CancellationToken cancellationToken)
    {
        path = ResolveAndValidatePath(path);

        var trashPath = await client.MoveToTrash(path, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Moved to trash",
            ["originalPath"] = path,
            ["trashPath"] = trashPath
        };
    }

    private string ResolveAndValidatePath(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{nameof(RemoveTool)} path must not contain '..' segments.");
        }

        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(libraryPath.BaseLibraryPath, path);
        }

        var canonicalLibraryPath = Path.GetFullPath(libraryPath.BaseLibraryPath);
        var canonicalFilePath = Path.GetFullPath(path);

        return !canonicalFilePath.StartsWith(canonicalLibraryPath, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException($"""
                                                   {nameof(RemoveTool)} path must be within the library.
                                                   Resolved path '{canonicalFilePath}' is not under library path '{canonicalLibraryPath}'.
                                                   """)
            : path;
    }
}
