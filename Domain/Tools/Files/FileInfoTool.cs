using System.Text.Json.Nodes;

namespace Domain.Tools.Files;

public class FileInfoTool(string rootPath)
{
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    protected const string Description = """
                                         Returns metadata about a path: exists, isDirectory, size (files only), and lastModified.
                                         Use as a cheap guard before read/edit/move/delete to avoid errors on missing paths.
                                         Works for files and directories; never throws on missing paths — returns exists=false instead.
                                         """;

    protected JsonNode Run(string path)
    {
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(rootPath, path));

        var canonicalRoot = Path.GetFullPath(rootPath);
        var rootWithSep = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;

        if (!fullPath.Equals(canonicalRoot, _pathComparison) &&
            !fullPath.StartsWith(rootWithSep, _pathComparison))
        {
            throw new UnauthorizedAccessException($"Access denied: path must be within {canonicalRoot}");
        }

        var fileExists = File.Exists(fullPath);
        var dirExists = !fileExists && Directory.Exists(fullPath);

        if (!fileExists && !dirExists)
        {
            return new JsonObject
            {
                ["exists"] = false,
                ["path"] = fullPath
            };
        }

        if (dirExists)
        {
            var dirInfo = new DirectoryInfo(fullPath);
            return new JsonObject
            {
                ["exists"] = true,
                ["isDirectory"] = true,
                ["path"] = fullPath,
                ["lastModified"] = dirInfo.LastWriteTimeUtc.ToString("O")
            };
        }

        var info = new FileInfo(fullPath);
        return new JsonObject
        {
            ["exists"] = true,
            ["isDirectory"] = false,
            ["path"] = fullPath,
            ["size"] = info.Length,
            ["lastModified"] = info.LastWriteTimeUtc.ToString("O")
        };
    }
}
