using Domain.DTOs;

namespace Domain.Contracts;

public interface IFileSystemClient
{
    Task<LibraryDescriptionNode> DescribeDirectory(string path);
    Task Move(string sourceFile, string destinationPath, CancellationToken cancellationToken = default);
}