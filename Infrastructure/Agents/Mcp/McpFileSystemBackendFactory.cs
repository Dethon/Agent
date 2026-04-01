using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;

namespace Infrastructure.Agents.Mcp;

public sealed class McpFileSystemBackendFactory(ILogger<McpFileSystemBackendFactory> logger) : IFileSystemBackendFactory
{
    private const string ResourcePrefix = "filesystem://";

    public async Task<IReadOnlyList<(FileSystemMount Mount, IFileSystemBackend Backend)>> DiscoverAsync(
        string endpoint, CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        var client = await retryPolicy.ExecuteAsync(() => McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "filesystem-discovery",
                    Description = "Filesystem backend discovery",
                    Version = "1.0.0"
                }
            },
            cancellationToken: ct));

        var resources = await client.ListResourcesAsync(cancellationToken: ct);
        var filesystemResources = resources
            .Where(r => r.Uri.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filesystemResources.Count == 0)
        {
            logger.LogWarning("No filesystem resources found at endpoint {Endpoint}", endpoint);
            await client.DisposeAsync();
            return [];
        }

        var results = new List<(FileSystemMount, IFileSystemBackend)>();

        foreach (var resource in filesystemResources)
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
            results.Add((mount, backend));

            logger.LogInformation("Discovered filesystem '{Name}' at mount point '{MountPoint}' from {Endpoint}",
                metadata.Name, metadata.MountPoint, endpoint);
        }

        return results;
    }

    private record FileSystemResourceMetadata(string Name, string MountPoint, string? Description);
}
