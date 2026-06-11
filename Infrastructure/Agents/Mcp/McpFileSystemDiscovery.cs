using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Agents.Mcp;
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

                var mount = new FileSystemMount(metadata.Name, metadata.MountPoint, metadata.Description ?? "");
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

    private record FileSystemResourceMetadata(string Name, string MountPoint, string? Description);
}