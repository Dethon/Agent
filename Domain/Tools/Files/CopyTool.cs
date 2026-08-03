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

    public FsResult<FsCopyResult> Run(string sourcePath, string destinationPath, bool overwrite, bool createDirectories)
    {
        if (!_jail.TryResolve(sourcePath, out var src) || !_jail.TryResolve(destinationPath, out var dst))
        {
            return FsError.Invalid<FsCopyResult>(_jail.DeniedMessage);
        }

        if (!File.Exists(src) && !Directory.Exists(src))
        {
            return FsError.NotFound<FsCopyResult>(sourcePath);
        }

        if (createDirectories)
        {
            var parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        var isFile = File.Exists(src);
        if (!overwrite && (isFile ? File.Exists(dst) : Directory.Exists(dst)))
        {
            return FsError.AlreadyExists<FsCopyResult>($"Destination already exists: {destinationPath}");
        }

        long bytes;
        if (isFile)
        {
            File.Copy(src, dst, overwrite);
            bytes = new FileInfo(dst).Length;
        }
        else
        {
            bytes = CopyDirectoryRecursive(src, dst, overwrite);
        }

        return new FsResult<FsCopyResult>.Ok(new FsCopyResult
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