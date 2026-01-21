using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpSubscriptionManager : IAsyncDisposable
{
    private readonly CancellationTokenSource _disposalCts = new();
    private readonly ConcurrentDictionary<McpClient, HashSet<string>> _subscribedResources = [];
    private int _isDisposed;

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

    private event Func<McpClient, JsonRpcNotification, CancellationToken, Task>? ResourceUpdated;
    private event Func<bool, CancellationToken, Task>? ResourcesSynced;

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

        var hasAnyResources = false;

        foreach (var client in clients)
        {
            if (client.ServerCapabilities.Resources is null)
            {
                continue;
            }

            var current = (await client.ListResourcesAsync(cancellationToken: ct))
                .Select(r => r.Uri)
                .ToHashSet();
            var previous = _subscribedResources.GetValueOrDefault(client) ?? [];

            foreach (var uri in current.Except(previous))
            {
                await client.SubscribeToResourceAsync(uri, cancellationToken: ct);
            }

            foreach (var uri in previous.Except(current))
            {
                await client.UnsubscribeFromResourceAsync(uri, cancellationToken: ct);
            }

            _subscribedResources[client] = current;
            hasAnyResources |= current.Count > 0;
        }

        if (ResourcesSynced != null)
        {
            await ResourcesSynced(hasAnyResources, ct);
        }
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