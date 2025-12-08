using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

internal sealed class McpResourceManager(
    Func<
        IEnumerable<ChatMessage>,
        CancellationToken,
        IAsyncEnumerable<AgentRunResponseUpdate>> runStreamingFunc)
    : IAsyncDisposable
{
    private ImmutableDictionary<McpClient, ImmutableHashSet<string>> _availableResources =
        ImmutableDictionary<McpClient, ImmutableHashSet<string>>.Empty;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private bool _isDisposed;

    public Channel<AgentRunResponseUpdate> SubscriptionChannel { get; private set; } = CreateChannel();

    public void SubscribeToNotifications(IEnumerable<McpClient> clients)
    {
        foreach (var client in clients)
        {
            client.RegisterNotificationHandler(
                "notifications/resources/updated",
                async (notification, ct) => await HandleResourceUpdated(client, notification, ct));

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
            if (client.ServerCapabilities.Resources is null) continue;

            var currentResources = await client.EnumerateResourcesAsync(ct)
                .Select(x => x.Uri)
                .ToArrayAsync(ct);
            var previousResources = _availableResources.GetValueOrDefault(client) ?? [];
            var newResources = currentResources.Except(previousResources);
            var removedResources = previousResources.Except(currentResources);

            foreach (var uri in newResources)
            {
                await client.SubscribeToResourceAsync(uri, ct);
            }

            foreach (var uri in removedResources)
            {
                await client.UnsubscribeFromResourceAsync(uri, ct);
            }

            _availableResources = _availableResources.SetItem(client, [.. currentResources]);
            hasAnyResources |= currentResources.Length > 0;
        }

        await UpdateChannelState(hasAnyResources, ct);
    }

    public void EnsureChannelActive()
    {
        if (SubscriptionChannel.Reader.Completion.IsCompleted)
        {
            SubscriptionChannel = CreateChannel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        SubscriptionChannel.Writer.TryComplete();
        _syncLock.Dispose();
        await UnsubscribeFromAllResources(CancellationToken.None);
    }

    private static Channel<AgentRunResponseUpdate> CreateChannel()
    {
        var options = new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest };
        return Channel.CreateBounded<AgentRunResponseUpdate>(options);
    }

    private async ValueTask HandleResourceUpdated(
        McpClient client,
        JsonRpcNotification notification,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var uri = notification.Params
            .Deserialize<Dictionary<string, string>>()?
            .GetValueOrDefault("uri");

        if (uri is null || SubscriptionChannel.Reader.Completion.IsCompleted) return;

        var resource = await client.ReadResourceAsync(uri, ct);
        var message = new ChatMessage(ChatRole.User, resource.Contents.ToAIContents());

        await _syncLock.WaitAsync(ct);
        try
        {
            await foreach (var update in runStreamingFunc([message], ct))
            {
                SubscriptionChannel.Writer.TryWrite(update);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task UpdateChannelState(bool hasAnyResources, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            if (!hasAnyResources)
                SubscriptionChannel.Writer.TryComplete();
            else if (SubscriptionChannel.Reader.Completion.IsCompleted)
                SubscriptionChannel = CreateChannel();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task UnsubscribeFromAllResources(CancellationToken ct)
    {
        foreach (var (client, uris) in _availableResources)
        {
            foreach (var uri in uris)
            {
                await client.UnsubscribeFromResourceAsync(uri, ct);
            }
        }
    }
}