using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;

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
            return FsResultContract.ToNode(new FsInfoResult { Exists = false, Path = fullPath });
        }

        if (dirExists)
        {
            var dirInfo = new DirectoryInfo(fullPath);
            return FsResultContract.ToNode(new FsInfoResult
            {
                Exists = true,
                Path = fullPath,
                IsDirectory = true,
                LastModified = dirInfo.LastWriteTimeUtc.ToString("O")
            });
        }

        var info = new FileInfo(fullPath);
        return FsResultContract.ToNode(new FsInfoResult
        {
            Exists = true,
            Path = fullPath,
            IsDirectory = false,
            Size = info.Length,
            LastModified = info.LastWriteTimeUtc.ToString("O")
        });
    }
}