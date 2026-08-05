using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

public record FileSystemResolution(IFileSystemBackend Backend, string RelativePath, string MountPoint = "");

public interface IVirtualFileSystemRegistry
{
    void Mount(FileSystemMount mount, IFileSystemBackend backend);

    // Resolution is data, not an exception: a path with no mount prefix is the mistake the
    // filesystem prompt warns the model about, and it must come back as the envelope the prompt
    // promises rather than unwinding twelve tool call sites that none of them guard.
    FsResult<FileSystemResolution> Resolve(string virtualPath);

    IReadOnlyList<FileSystemMount> GetMounts();
}