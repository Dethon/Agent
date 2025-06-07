namespace Domain.Contracts;

public interface IFileSystemClient
{
    Task<Dictionary<string, string[]>> DescribeDirectory(string path, CancellationToken cancellationToken = default);
    Task<string[]> ListDirectoriesIn(string path, CancellationToken cancellationToken = default);
    Task<string[]> ListFilesIn(string path, CancellationToken cancellationToken = default);
    Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task RemoveDirectory(string path, CancellationToken cancellationToken = default);
    Task RemoveFile(string path, CancellationToken cancellationToken = default);
}