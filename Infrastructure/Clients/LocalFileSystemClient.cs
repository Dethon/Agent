using Domain.Contracts;

namespace Infrastructure.Clients;

public class LocalFileSystemClient : IFileSystemClient
{
    public Task<Dictionary<string, string[]>> DescribeDirectory(string path,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Library directory not found: {path}");
        }

        return Task.FromResult(GetLibraryPaths(path));
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