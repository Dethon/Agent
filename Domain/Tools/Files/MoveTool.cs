using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class MoveTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    private readonly PathJail _jail = new(libraryPath.BaseLibraryPath);

    protected const string Description = """
                                         Moves and/or renames a file or directory.
                                         Both arguments can be absolute paths under the library root, or relative paths
                                         (resolved against the library root).
                                         Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
                                         The destination path must not exist. Parent directories are created automatically.
                                         """;

    public async Task<FsResult<FsMoveResult>> Run(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (Validate(sourcePath) is { } sourceError)
        {
            return sourceError;
        }

        if (Validate(destinationPath) is { } destinationError)
        {
            return destinationError;
        }

        sourcePath = Combine(sourcePath);
        destinationPath = Combine(destinationPath);

        try
        {
            await client.Move(sourcePath, destinationPath, ct);
        }
        catch (IOException ex)
        {
            // The client signals a missing source this way; the model needs the code, not the type.
            return FsError.Fail<FsMoveResult>(ToolError.Codes.NotFound, ex.Message);
        }

        return new FsResult<FsMoveResult>.Ok(new FsMoveResult
        {
            Status = "success",
            Message = "File moved successfully",
            Source = sourcePath,
            Destination = destinationPath
        });
    }

    private FsResult<FsMoveResult>? Validate(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return FsError.Invalid<FsMoveResult>($"{nameof(MoveTool)} path must not contain '..' segments.");
        }

        return _jail.Contains(Path.GetFullPath(Combine(path)))
            ? null
            : FsError.Invalid<FsMoveResult>($"""
                                             {nameof(MoveTool)} path must be within the library.
                                             Resolved path '{Path.GetFullPath(Combine(path))}' is not under library path '{_jail.Root}'.
                                             """);
    }

    // Kept mount-relative rather than canonical: the client and the reported result both use the
    // caller's spelling, and only containment is decided on the canonical form.
    private string Combine(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(libraryPath.BaseLibraryPath, path);
}