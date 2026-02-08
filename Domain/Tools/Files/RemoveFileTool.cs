using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class RemoveFileTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "RemoveFile";

    protected const string Description = """
                                         Removes a file by moving it to a trash folder.
                                         The path can be absolute (under the library root) or relative
                                         (resolved against the library root).
                                         """;

    protected async Task<JsonNode> Run(string filePath, CancellationToken cancellationToken)
    {
        filePath = ResolveAndValidatePath(filePath);

        var trashPath = await client.MoveToTrash(filePath, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File moved to trash",
            ["originalPath"] = filePath,
            ["trashPath"] = trashPath
        };
    }

    private string ResolveAndValidatePath(string filePath)
    {
        if (filePath.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{nameof(RemoveFileTool)} path must not contain '..' segments.");
        }

        if (!Path.IsPathRooted(filePath))
        {
            filePath = Path.Combine(libraryPath.BaseLibraryPath, filePath);
        }

        var canonicalLibraryPath = Path.GetFullPath(libraryPath.BaseLibraryPath);
        var canonicalFilePath = Path.GetFullPath(filePath);

        if (!canonicalFilePath.StartsWith(canonicalLibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"""
                                                 {nameof(RemoveFileTool)} path must be within the library.
                                                 Resolved path '{canonicalFilePath}' is not under library path '{canonicalLibraryPath}'.
                                                 """);
        }

        return filePath;
    }
}