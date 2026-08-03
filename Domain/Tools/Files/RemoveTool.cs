using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class RemoveTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    private readonly PathJail _jail = new(libraryPath.BaseLibraryPath);

    protected const string Description = """
                                         Removes a file or directory by moving it to a trash folder.
                                         The path can be absolute (under the library root) or relative
                                         (resolved against the library root).
                                         """;

    protected async Task<JsonNode> Run(string path, CancellationToken cancellationToken)
    {
        path = ResolveAndValidatePath(path);

        var trashPath = await client.MoveToTrash(path, cancellationToken);
        return FsResultContract.ToNode(new FsRemoveResult
        {
            Status = "success",
            Message = "Moved to trash",
            OriginalPath = path,
            TrashPath = trashPath
        });
    }

    private string ResolveAndValidatePath(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{nameof(RemoveTool)} path must not contain '..' segments.");
        }

        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(libraryPath.BaseLibraryPath, path);
        }

        var canonicalFilePath = Path.GetFullPath(path);

        return !_jail.Contains(canonicalFilePath)
            ? throw new ArgumentException($"""
                                           {nameof(RemoveTool)} path must be within the library.
                                           Resolved path '{canonicalFilePath}' is not under library path '{_jail.Root}'.
                                           """)
            : path;
    }
}