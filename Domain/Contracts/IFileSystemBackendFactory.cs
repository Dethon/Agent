using Domain.DTOs;

namespace Domain.Contracts;

public interface IFileSystemBackendFactory
{
    Task<IReadOnlyList<(FileSystemMount Mount, IFileSystemBackend Backend)>> DiscoverAsync(
        string endpoint, CancellationToken ct);
}
