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
        throw new NotImplementedException();
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
                Name = Path.GetDirectoryName(directory) ??
                       throw new DirectoryNotFoundException($"Directory name not found: {directory}"),
                Type = LibraryEntryType.Directory,
                Children = GetLibraryChildNodes(directory)
            })
            .Concat(fileNodes)
            .ToArray();
    }
}