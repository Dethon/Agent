using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients;

public class LocalFileSystemClient : IFileSystemClient
{
    public Task<LibraryDescriptionNode> DescribeDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Library directory not found: {path}");
        }

        return Task.FromResult(new LibraryDescriptionNode
        {
            Name = Path.GetFileName(path),
            Type = LibraryEntryType.Directory,
            Children = GetLibraryChildNodes(path)
        });
    }

    public Task Move(string sourceFile, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFile) && !Directory.Exists(sourceFile))
        {
            throw new IOException("Source file does not exist");
        }

        CreateDestinationParentPath(destinationPath);

        if (File.Exists(sourceFile))
        {
            File.Move(sourceFile, destinationPath);
        }
        else if (Directory.Exists(sourceFile))
        {
            Directory.Move(sourceFile, destinationPath);
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

    private static LibraryDescriptionNode[] GetLibraryChildNodes(string basePath)
    {
        var fileNodes = Directory.GetFiles(basePath)
            .Select(file => new LibraryDescriptionNode
            {
                Name = Path.GetFileName(file),
                Type = LibraryEntryType.File
            });
        return Directory.GetDirectories(basePath)
            .Select(directory => new LibraryDescriptionNode
            {
                Name = Path.GetFileName(directory) ??
                       throw new DirectoryNotFoundException($"Directory name not found: {directory}"),
                Type = LibraryEntryType.Directory,
                Children = GetLibraryChildNodes(directory)
            })
            .Concat(fileNodes)
            .ToArray();
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