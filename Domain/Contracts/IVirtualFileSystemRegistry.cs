using Domain.DTOs;

namespace Domain.Contracts;

public interface IVirtualFileSystemRegistry
{
    void Mount(FileSystemMount mount, IFileSystemBackend backend);
    FileSystemResolution Resolve(string virtualPath);
    IReadOnlyList<FileSystemMount> GetMounts();
}
