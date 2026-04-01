using Domain.DTOs;

namespace Domain.Contracts;

public interface IVirtualFileSystemRegistry
{
    Task DiscoverAsync(string[] endpoints, IFileSystemBackendFactory backendFactory, CancellationToken ct);
    FileSystemResolution Resolve(string virtualPath);
    IReadOnlyList<FileSystemMount> GetMounts();
}
