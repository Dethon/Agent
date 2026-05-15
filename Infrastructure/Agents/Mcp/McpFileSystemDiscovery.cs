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
        foreach (var client in clients.Where(c => c.ServerCapabilities.Resources is not null))
        {
            var resources = await client.ListResourcesAsync(cancellationToken: ct);
            var filesystemResources = resources
                .Where(r => r.Uri.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filesystemResources.Count == 0)
            {
                continue;
            }

            foreach (var resource in filesystemResources)
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
                        continue;
                    }

                    var mount = new FileSystemMount(metadata.Name, metadata.MountPoint, metadata.Description ?? "");
                    var backend = new McpFileSystemBackend(client, metadata.Name);
                    registry.Mount(mount, backend);

                    logger.LogInformation("Discovered filesystem '{Name}' at mount point '{MountPoint}'",
                        metadata.Name, metadata.MountPoint);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read filesystem resource at {Uri}", resource.Uri);
                }
            }
        }
    }

    private record FileSystemResourceMetadata(string Name, string MountPoint, string? Description);
}