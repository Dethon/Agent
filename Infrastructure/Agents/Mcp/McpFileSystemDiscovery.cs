using System.Text.Json;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

internal static class McpFileSystemDiscovery
{
    private const string ResourcePrefix = "filesystem://";

    public static async Task DiscoverAndMountAsync(
        IReadOnlyList<McpClient> clients,
        VirtualFileSystemRegistry registry,
        ILogger logger,
        CancellationToken ct)
    {
        var perClient = await Task.WhenAll(clients
            .Where(c => c.ServerCapabilities.Resources is not null)
            .Select(client => GatherMountsAsync(client, logger, ct)));

        foreach (var (mount, backend) in perClient.SelectMany(m => m))
        {
            registry.Mount(mount, backend);
            logger.LogInformation("Discovered filesystem '{Name}' at mount point '{MountPoint}'",
                mount.Name, mount.MountPoint);
        }
    }

    private static async Task<IReadOnlyList<(FileSystemMount Mount, McpFileSystemBackend Backend)>> GatherMountsAsync(
        McpClient client, ILogger logger, CancellationToken ct)
    {
        var resources = await client.ListResourcesAsync(cancellationToken: ct);
        var filesystemResources = resources
            .Where(r => r.Uri.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filesystemResources.Count == 0)
        {
            return [];
        }

        // Tool registration is per-server, so the same capability set applies to every filesystem
        // this client exposes; list once.
        var advertisedTools = await client.ListToolsAsync(cancellationToken: ct);
        var capabilities = DeriveCapabilities(advertisedTools.Select(t => t.Name));

        var mounts = await Task.WhenAll(filesystemResources.Select(async resource =>
        {
            try
            {
                var content = await client.ReadResourceAsync(resource.Uri, cancellationToken: ct);
                var text = string.Join("", content.Contents
                    .OfType<TextResourceContents>()
                    .Select(c => c.Text));

                var metadata = JsonSerializer.Deserialize<FileSystemResourceMetadata>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (metadata is null || string.IsNullOrEmpty(metadata.Name) || string.IsNullOrEmpty(metadata.MountPoint))
                {
                    logger.LogWarning("Invalid filesystem resource metadata at {Uri}", resource.Uri);
                    return ((FileSystemMount Mount, McpFileSystemBackend Backend)?)null;
                }

                var mount = new FileSystemMount(metadata.Name, metadata.MountPoint, metadata.Description ?? "")
                {
                    Capabilities = capabilities
                };
                var backend = new McpFileSystemBackend(client, metadata.Name, logger);
                return (mount, backend);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read filesystem resource at {Uri}", resource.Uri);
                return null;
            }
        }));

        return mounts.Where(m => m is not null).Select(m => m!.Value).ToList();
    }

    // A server advertises exactly the fs_* tools it supports (unsupported ones are never registered),
    // so its advertised tool set is the single source of truth for a mount's capabilities. Map each
    // fs_* tool to the domain-tool leaf name the LLM actually calls, in a stable display order.
    private static readonly (string FsTool, string Capability)[] _capabilityMap =
    [
        ("fs_read", VfsTextReadTool.Name),
        ("fs_create", VfsTextCreateTool.Name),
        ("fs_edit", VfsTextEditTool.Name),
        ("fs_glob", VfsGlobFilesTool.Name),
        ("fs_search", VfsTextSearchTool.Name),
        ("fs_move", VfsMoveTool.Name),
        ("fs_copy", VfsCopyTool.Name),
        ("fs_delete", VfsRemoveTool.Name),
        ("fs_info", VfsFileInfoTool.Name),
        ("fs_exec", VfsExecTool.Name)
    ];

    internal static IReadOnlyList<string> DeriveCapabilities(IEnumerable<string> advertisedToolNames)
    {
        var advertised = advertisedToolNames.ToList();
        return _capabilityMap
            .Where(m => advertised.Any(name =>
                name.Equals(m.FsTool, StringComparison.Ordinal) ||
                name.EndsWith($"__{m.FsTool}", StringComparison.Ordinal)))
            .Select(m => m.Capability)
            .ToList();
    }

    private record FileSystemResourceMetadata(string Name, string MountPoint, string? Description);
}