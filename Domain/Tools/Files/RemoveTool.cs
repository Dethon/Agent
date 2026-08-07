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
        // No '..' screening here, exactly as in MoveTool: the jail judges the canonical resolved
        // path, so a traversal segment is caught by where it lands, and a name like v1..2.mkv is
        // just a name — one this tool used to refuse for a file it had happily created and moved.
        path = Path.IsPathRooted(path) ? path : Path.Combine(libraryPath.BaseLibraryPath, path);
        var canonical = Path.GetFullPath(path);

        if (!_jail.Contains(canonical))
        {
            return FsError.Invalid<FsRemoveResult>($"""
                                                    {nameof(RemoveTool)} path must be within the library.
                                                    Resolved path '{canonical}' is not under library path '{_jail.Root}'.
                                                    """);
        }

        try
        {
            await client.MoveToTrash(path, cancellationToken);
        }
        catch (IOException ex)
        {
            // The client signals a missing path this way; the model needs the code, not the type.
            return FsError.Fail<FsRemoveResult>(ToolError.Codes.NotFound, ex.Message);
        }

        // No trash path: it sits outside every mount, so no virtual path for it exists and the model
        // cannot read or restore from it. The three virtual filesystems already answer empty here;
        // the disk root joins them rather than reporting a location that looks actionable.
        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "success",
            Message = "Moved to trash",
            OriginalPath = path,
            TrashPath = ""
        });
    }
}