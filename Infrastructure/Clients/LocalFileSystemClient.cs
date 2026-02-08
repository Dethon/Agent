using Domain.Contracts;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Infrastructure.Clients;

public class LocalFileSystemClient : IFileSystemClient
{
    public const string TrashFolderName = ".trash";

    public Task<Dictionary<string, string[]>> DescribeDirectory(string path,
        CancellationToken cancellationToken = default)
    {
        return !Directory.Exists(path)
            ? throw new DirectoryNotFoundException($"Library directory not found: {path}")
            : Task.FromResult(GetLibraryPaths(path));
    }

    public Task<string[]> GlobFiles(string basePath, string pattern, CancellationToken cancellationToken = default)
    {
        var matcher = new Matcher();
        matcher.AddInclude(pattern);
        var result = matcher.GetResultsInFullPath(basePath);
        return Task.FromResult(result.ToArray());
    }

    public Task<string[]> GlobDirectories(string basePath, string pattern, CancellationToken cancellationToken = default)
    {
        var matcher = new Matcher();
        matcher.AddInclude(pattern);

        var dirsFromFiles = matcher.GetResultsInFullPath(basePath)
            .Select(f => Path.GetDirectoryName(f)!);

        var dirRelativePaths = Directory.EnumerateDirectories(basePath, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(basePath, d));
        var dirsFromDirectories = matcher.Match(basePath, dirRelativePaths)
            .Files
            .Select(f => Path.GetFullPath(Path.Combine(basePath, f.Path)));

        var result = dirsFromFiles
            .Concat(dirsFromDirectories)
            .Where(d => d != basePath)
            .Distinct()
            .Order()
            .ToArray();
        return Task.FromResult(result);
    }

    public Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new IOException($"Source path {sourcePath} does not exist");
        }

        CreateDestinationParentPath(destinationPath);
        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath);
        }
        else if (Directory.Exists(sourcePath))
        {
            MoveDirectory(sourcePath, destinationPath);
        }

        return Task.CompletedTask;
    }

    public Task RemoveDirectory(string path, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        return Task.CompletedTask;
    }

    public Task RemoveFile(string path, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<string> MoveToTrash(string path, CancellationToken cancellationToken = default)
    {
        var isFile = File.Exists(path);
        var isDirectory = Directory.Exists(path);

        if (!isFile && !isDirectory)
        {
            throw new IOException($"Path not found: {path}");
        }

        var name = Path.GetFileName(path);
        var trashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), TrashFolderName);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var trashPath = Path.Combine(trashDir, $"{timestamp}_{uniqueId}_{name}");

        CreateDestinationParentPath(trashPath);

        if (isFile)
        {
            File.Move(path, trashPath);
        }
        else
        {
            MoveDirectory(path, trashPath);
        }

        return Task.FromResult(trashPath);
    }

    private static Dictionary<string, string[]> GetLibraryPaths(string basePath)
    {
        // ReSharper disable once ConvertClosureToMethodGroup | It messes with the nullability checks somehow
        return Directory
            .EnumerateFiles(basePath, "*", SearchOption.AllDirectories)
            .GroupBy(
                x => Path.GetDirectoryName(x) ?? string.Empty,
                x => Path.GetFileName(x))
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .ToDictionary(
                x => x.Key,
                x => x.Where(y => !string.IsNullOrEmpty(y)).ToArray());
    }

    private static void MoveDirectory(string sourcePath, string destinationPath)
    {
        try
        {
            Directory.Move(sourcePath, destinationPath);
        }
        catch (IOException)
        {
            // Directory.Move fails with EXDEV across filesystem boundaries;
            // fall back to recursive copy + delete
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, true);
        }
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var file in Directory.EnumerateFiles(sourcePath))
        {
            File.Copy(file, Path.Combine(destinationPath, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(sourcePath))
        {
            CopyDirectory(dir, Path.Combine(destinationPath, Path.GetFileName(dir)));
        }
    }

    private static void CreateDestinationParentPath(string destinationPath)
    {
        var parentPath = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(parentPath) || Directory.Exists(parentPath) || File.Exists(parentPath))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(parentPath);
            return;
        }

        const UnixFileMode mode = UnixFileMode.UserRead |
                                  UnixFileMode.UserWrite |
                                  UnixFileMode.UserExecute |
                                  UnixFileMode.GroupRead |
                                  UnixFileMode.GroupWrite |
                                  UnixFileMode.GroupExecute |
                                  UnixFileMode.OtherRead |
                                  UnixFileMode.OtherExecute;
        Directory.CreateDirectory(parentPath, mode);
    }
}