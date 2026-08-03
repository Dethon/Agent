using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.Files;

public class CopyTool(string rootPath)
{
    private readonly PathJail _jail = new(rootPath);

    protected const string Description = """
        Copies a file or directory within this filesystem.
        Both arguments can be absolute paths under the filesystem root, or relative paths
        (resolved against the root). Source must exist; if destination exists, overwrite must be true.
        Parent directories are created automatically when createDirectories=true (default).
        """;

    protected JsonNode Run(string sourcePath, string destinationPath, bool overwrite, bool createDirectories)
    {
        var src = _jail.Resolve(sourcePath);
        var dst = _jail.Resolve(destinationPath);

        if (!File.Exists(src) && !Directory.Exists(src))
        {
            throw new IOException($"Source path does not exist: {sourcePath}");
        }

        if (createDirectories)
        {
            var parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        long bytes;
        if (File.Exists(src))
        {
            if (File.Exists(dst) && !overwrite)
            {
                throw new IOException($"Destination already exists: {destinationPath}");
            }

            File.Copy(src, dst, overwrite);
            bytes = new FileInfo(dst).Length;
        }
        else
        {
            if (Directory.Exists(dst) && !overwrite)
            {
                throw new IOException($"Destination already exists: {destinationPath}");
            }

            bytes = CopyDirectoryRecursive(src, dst, overwrite);
        }

        return FsResultContract.ToNode(new FsCopyResult
        {
            Status = "copied",
            Source = sourcePath,
            Destination = destinationPath,
            Bytes = bytes
        });
    }

    private static long CopyDirectoryRecursive(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        var fileBytes = Directory.EnumerateFiles(source).Sum(f =>
        {
            var target = Path.Combine(destination, Path.GetFileName(f));
            File.Copy(f, target, overwrite);
            return new FileInfo(target).Length;
        });
        var dirBytes = Directory.EnumerateDirectories(source).Sum(d =>
            CopyDirectoryRecursive(d, Path.Combine(destination, Path.GetFileName(d)), overwrite));
        return fileBytes + dirBytes;
    }
}