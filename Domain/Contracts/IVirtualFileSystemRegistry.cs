using Domain.DTOs;

namespace Domain.Contracts;

public record FileSystemResolution(IFileSystemBackend Backend, string RelativePath);

public interface IVirtualFileSystemRegistry
{
    void Mount(FileSystemMount mount, IFileSystemBackend backend);
    FileSystemResolution Resolve(string virtualPath);
    IReadOnlyList<FileSystemMount> GetMounts();
}