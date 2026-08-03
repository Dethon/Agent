using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
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

    public async Task<FsResult<FsRemoveResult>> Run(string path, CancellationToken cancellationToken)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return FsError.Invalid<FsRemoveResult>($"{nameof(RemoveTool)} path must not contain '..' segments.");
        }

        path = Path.IsPathRooted(path) ? path : Path.Combine(libraryPath.BaseLibraryPath, path);
        var canonical = Path.GetFullPath(path);

        if (!_jail.Contains(canonical))
        {
            return FsError.Invalid<FsRemoveResult>($"""
                                                    {nameof(RemoveTool)} path must be within the library.
                                                    Resolved path '{canonical}' is not under library path '{_jail.Root}'.
                                                    """);
        }

        string trashPath;
        try
        {
            trashPath = await client.MoveToTrash(path, cancellationToken);
        }
        catch (IOException ex)
        {
            // The client signals a missing path this way; the model needs the code, not the type.
            return FsError.Fail<FsRemoveResult>(ToolError.Codes.NotFound, ex.Message);
        }

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "success",
            Message = "Moved to trash",
            OriginalPath = path,
            TrashPath = trashPath
        });
    }
}