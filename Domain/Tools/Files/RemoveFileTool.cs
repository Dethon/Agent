using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class RemoveFileTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "RemoveFile";

    protected const string Description = """
                                         Removes a file by moving it to a trash folder.
                                         The path must be absolute and derived from the ListFiles tool response.
                                         """;

    protected async Task<JsonNode> Run(string filePath, CancellationToken cancellationToken)
    {
        ValidatePathWithinLibrary(filePath);

        var trashPath = await client.MoveToTrash(filePath, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File moved to trash",
            ["originalPath"] = filePath,
            ["trashPath"] = trashPath
        };
    }

    private void ValidatePathWithinLibrary(string filePath)
    {
        if (filePath.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{nameof(RemoveFileTool)} path must not contain '..' segments.");
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
    }
}