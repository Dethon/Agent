using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpSubscriptionManager : IAsyncDisposable
{
    private int _isDisposed;
    private readonly CancellationTokenSource _disposalCts = new();
    private readonly ConcurrentDictionary<McpClient, HashSet<string>> _subscribedResources = [];
    private event Func<McpClient, JsonRpcNotification, CancellationToken, Task>? ResourceUpdated;
    private event Func<bool, CancellationToken, Task>? ResourcesSynced;

    public McpSubscriptionManager(ResourceUpdateProcessor processor)
    {
        ResourceUpdated += processor.HandleResourceUpdatedAsync;
        ResourcesSynced += processor.HandleResourcesSyncedAsync;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        ResourceUpdated = null;
        ResourcesSynced = null;

        await _disposalCts.CancelAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await UnsubscribeFromAllResources(timeoutCts.Token);

        _disposalCts.Dispose();
    }

    public void SubscribeToNotifications(IEnumerable<McpClient> clients)
    {
        foreach (var client in clients)
        {
            client.RegisterNotificationHandler(
                "notifications/resources/updated",
                async (notification, ct) =>
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposalCts.Token);
                    if (ResourceUpdated != null)
                    {
                        await ResourceUpdated(client, notification, linkedCts.Token);
                    }
                });

            client.RegisterNotificationHandler(
                "notifications/resources/list_changed",
                async (_, ct) =>
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposalCts.Token);
                    await SyncResourcesAsync([client], linkedCts.Token);
                });
        }
    }

    public async Task SyncResourcesAsync(IEnumerable<McpClient> clients, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        var clientHasResources = await Task.WhenAll(clients
            .Where(c => c.ServerCapabilities.Resources is { Subscribe: true })
            .Select(c => SyncClientAsync(c, ct)));

        if (ResourcesSynced != null)
        {
            await ResourcesSynced(clientHasResources.Any(r => r), ct);
        }
    }

    private async Task<bool> SyncClientAsync(McpClient client, CancellationToken ct)
    {
        var current = (await client.ListResourcesAsync(cancellationToken: ct))
            .Where(r => !r.Uri.StartsWith("filesystem://", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Uri)
            .ToHashSet();
        var previous = _subscribedResources.GetValueOrDefault(client) ?? [];

        // Concurrent requests on one client are safe: the MCP session multiplexes
        // JSON-RPC calls, same as the parallel per-client prompt fetches.
        await Task.WhenAll(current.Except(previous)
            .Select(uri => client.SubscribeToResourceAsync(uri, cancellationToken: ct)));
        await Task.WhenAll(previous.Except(current)
            .Select(uri => client.UnsubscribeFromResourceAsync(uri, cancellationToken: ct)));

        _subscribedResources[client] = current;
        return current.Count > 0;
    }

    private async Task UnsubscribeFromAllResources(CancellationToken ct)
    {
        foreach (var (client, uris) in _subscribedResources)
        {
            foreach (var uri in uris)
            {
                await client.UnsubscribeFromResourceAsync(uri, cancellationToken: ct);
            }
        }
    }
}