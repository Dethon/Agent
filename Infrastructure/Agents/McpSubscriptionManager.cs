using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

internal sealed class McpSubscriptionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<McpClient, HashSet<string>> _subscribedResources = [];

    private bool _isDisposed;

    public event Func<McpClient, JsonRpcNotification, CancellationToken, Task>? ResourceUpdated;
    public event Func<bool, CancellationToken, Task>? ResourcesSynced;

    public void SubscribeToNotifications(IEnumerable<McpClient> clients)
    {
        foreach (var client in clients)
        {
            client.RegisterNotificationHandler(
                "notifications/resources/updated",
                async (notification, ct) =>
                {
                    if (ResourceUpdated is not null)
                    {
                        await ResourceUpdated(client, notification, ct);
                    }
                });

            client.RegisterNotificationHandler(
                "notifications/resources/list_changed",
                async (_, ct) => await SyncResourcesAsync([client], ct));
        }
    }

    public async Task SyncResourcesAsync(IEnumerable<McpClient> clients, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

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

        if (ResourcesSynced is not null)
        {
            await ResourcesSynced(hasAnyResources, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await UnsubscribeFromAllResources(cts.Token);
        cts.Dispose();
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