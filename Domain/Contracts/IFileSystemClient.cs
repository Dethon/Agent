namespace Domain.Contracts;

public interface IFileSystemClient
{
    Task<string[]> DescribeDirectory(string path, CancellationToken cancellationToken = default);
    Task Move(string sourceFile, string destinationPath, CancellationToken cancellationToken = default);
    Task RemoveDirectory(string path, CancellationToken cancellationToken = default);
    Task RemoveFile(string path, CancellationToken cancellationToken = default);
}