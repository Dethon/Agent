using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Agents;

internal sealed class VirtualFileSystemRegistry : IVirtualFileSystemRegistry
{
    private readonly Dictionary<string, (FileSystemMount Mount, IFileSystemBackend Backend)> _mounts =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task DiscoverAsync(string[] endpoints, IFileSystemBackendFactory backendFactory, CancellationToken ct)
    {
        foreach (var endpoint in endpoints)
        {
            var discovered = await backendFactory.DiscoverAsync(endpoint, ct);
            foreach (var (mount, backend) in discovered)
                _mounts[mount.MountPoint] = (mount, backend);
        }
    }

    public FileSystemResolution Resolve(string virtualPath)
    {
        var match = _mounts
            .Where(m => virtualPath.StartsWith(m.Key, StringComparison.OrdinalIgnoreCase)
                && (virtualPath.Length == m.Key.Length || virtualPath[m.Key.Length] == '/'))
            .OrderByDescending(m => m.Key.Length)
            .Select(m => (FileSystemResolution?)new FileSystemResolution(
                m.Value.Backend,
                virtualPath[m.Key.Length..].TrimStart('/')))
            .FirstOrDefault();

        return match ?? throw new InvalidOperationException(
            $"No filesystem mounted for path '{virtualPath}'. Available: {FormatMounts()}");
    }

    public IReadOnlyList<FileSystemMount> GetMounts()
        => _mounts.Values.Select(v => v.Mount).ToList();

    private string FormatMounts()
        => string.Join(", ", _mounts.Values.Select(v => $"{v.Mount.MountPoint} ({v.Mount.Name})"));
}
