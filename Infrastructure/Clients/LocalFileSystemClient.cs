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

    public Task<string[]> ListDirectoriesIn(string path, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Directory
            .EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray());
    }

    public Task<string[]> ListFilesIn(string path, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Directory
            .EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray());
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
            Directory.Move(sourcePath, destinationPath);
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
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        var fileName = Path.GetFileName(path);
        var trashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), TrashFolderName);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var trashPath = Path.Combine(trashDir, $"{timestamp}_{uniqueId}_{fileName}");

        CreateDestinationParentPath(trashPath);
        File.Move(path, trashPath);

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