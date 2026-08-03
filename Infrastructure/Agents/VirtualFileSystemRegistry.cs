using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;

namespace Infrastructure.Agents;

internal sealed class VirtualFileSystemRegistry : IVirtualFileSystemRegistry
{
    private readonly Dictionary<string, (FileSystemMount Mount, IFileSystemBackend Backend)> _mounts =
        new(StringComparer.OrdinalIgnoreCase);

    public void Mount(FileSystemMount mount, IFileSystemBackend backend)
    {
        _mounts[mount.MountPoint] = (mount, backend);
    }

    public FsResult<FileSystemResolution> Resolve(string virtualPath)
    {
        var match = _mounts
            .Where(m => virtualPath.StartsWith(m.Key, StringComparison.OrdinalIgnoreCase)
                && (virtualPath.Length == m.Key.Length || virtualPath[m.Key.Length] == '/'))
            .OrderByDescending(m => m.Key.Length)
            .Select(m => new FileSystemResolution(
                m.Value.Backend,
                virtualPath[m.Key.Length..].TrimStart('/'),
                m.Key))
            .FirstOrDefault();

        return match is not null
            ? new FsResult<FileSystemResolution>.Ok(match)
            : new FsResult<FileSystemResolution>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.InvalidArgument,
                Message = $"No filesystem mounted for path '{virtualPath}'. Available: {FormatMounts()}",
                Retryable = false,
                Hint = "Virtual paths must start with a mount point; retry with one of the mounts listed."
            });
    }

    public IReadOnlyList<FileSystemMount> GetMounts()
        => _mounts.Values.Select(v => v.Mount).ToList();

    private string FormatMounts()
        => string.Join(", ", _mounts.Values.Select(v => $"{v.Mount.MountPoint} ({v.Mount.Name})"));
}