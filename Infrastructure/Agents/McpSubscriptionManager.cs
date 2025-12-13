using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

internal sealed class McpSubscriptionManager : IAsyncDisposable
{
    private readonly CancellationTokenSource _disposalCts = new();
    private readonly ConcurrentDictionary<McpClient, HashSet<string>> _subscribedResources = [];

    private int _isDisposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        await _disposalCts.CancelAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await UnsubscribeFromAllResources(timeoutCts.Token);

        _disposalCts.Dispose();
    }

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
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposalCts.Token);
                    await InvokeResourceUpdatedAsync(client, notification, linkedCts.Token);
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
                continue;

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
            await InvokeResourcesSyncedAsync(hasAnyResources, ct);
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

    private Task InvokeResourceUpdatedAsync(McpClient client, JsonRpcNotification notification, CancellationToken ct)
    {
        var handlers = ResourceUpdated?.GetInvocationList();
        if (handlers is null or { Length: 0 })
        {
            return Task.CompletedTask;
        }

        var tasks = handlers
            .Cast<Func<McpClient, JsonRpcNotification, CancellationToken, Task>>()
            .Select(handler => handler(client, notification, ct));

        return Task.WhenAll(tasks);
    }

    private Task InvokeResourcesSyncedAsync(bool hasAnyResources, CancellationToken ct)
    {
        var handlers = ResourcesSynced?.GetInvocationList();
        if (handlers is null or { Length: 0 })
        {
            return Task.CompletedTask;
        }

        var tasks = handlers
            .Cast<Func<bool, CancellationToken, Task>>()
            .Select(handler => handler(hasAnyResources, ct));

        return Task.WhenAll(tasks);
    }
}