using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class MoveTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "Move";

    protected const string Description = """
                                         Moves and/or renames a file or directory.
                                         Both arguments can be absolute paths under the library root, or relative paths
                                         (resolved against the library root).
                                         Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
                                         The destination path must not exist. Parent directories are created automatically.
                                         """;

    protected async Task<JsonNode> Run(string sourcePath, string destinationPath, CancellationToken ct)
    {
        sourcePath = ResolveAndValidatePath(sourcePath);
        destinationPath = ResolveAndValidatePath(destinationPath);

        await client.Move(sourcePath, destinationPath, ct);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File moved successfully",
            ["source"] = sourcePath,
            ["destination"] = destinationPath
        };
    }

    private string ResolveAndValidatePath(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{nameof(MoveTool)} path must not contain '..' segments.");
        }

        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(libraryPath.BaseLibraryPath, path);
        }

        var canonicalPath = Path.GetFullPath(path);
        var canonicalLibraryPath = Path.GetFullPath(libraryPath.BaseLibraryPath);

        if (!canonicalPath.StartsWith(canonicalLibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"""
                                                 {nameof(MoveTool)} path must be within the library.
                                                 Resolved path '{canonicalPath}' is not under library path '{canonicalLibraryPath}'.
                                                 """);
        }

        return path;
    }
}