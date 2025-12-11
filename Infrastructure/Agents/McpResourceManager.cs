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
    AIAgent agent,
    AgentThread thread,
    string? instructions,
    IReadOnlyList<AITool> tools) : IAsyncDisposable
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private ImmutableDictionary<McpClient, ImmutableHashSet<string>> _subscribedResources =
        ImmutableDictionary<McpClient, ImmutableHashSet<string>>.Empty;

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
            if (client.ServerCapabilities.Resources is null)
            {
                continue;
            }

            var current = (await client.ListResourcesAsync(cancellationToken: ct))
                .Select(r => r.Uri)
                .ToArray();
            var previous = _subscribedResources.GetValueOrDefault(client) ?? [];

            foreach (var uri in current.Except(previous))
            {
                await client.SubscribeToResourceAsync(uri, cancellationToken: ct);
            }

            foreach (var uri in previous.Except(current))
            {
                await client.UnsubscribeFromResourceAsync(uri, cancellationToken: ct);
            }

            _subscribedResources = _subscribedResources.SetItem(client, [..current]);
            hasAnyResources |= current.Length > 0;
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
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        SubscriptionChannel.Writer.TryComplete();
        _syncLock.Dispose();
        await UnsubscribeFromAllResources();
    }

    private static Channel<AgentRunResponseUpdate> CreateChannel()
    {
        return Channel.CreateBounded<AgentRunResponseUpdate>(
            new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
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

        if (uri is null || SubscriptionChannel.Reader.Completion.IsCompleted)
        {
            return;
        }

        var resource = await client.ReadResourceAsync(uri, cancellationToken: ct);
        var message = new ChatMessage(ChatRole.User, resource.Contents.ToAIContents());
        var options = new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [..tools],
            Instructions = instructions
        });

        await _syncLock.WaitAsync(ct);
        try
        {
            await foreach (var update in agent.RunStreamingAsync([message], thread, options, ct))
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
            {
                SubscriptionChannel.Writer.TryComplete();
            }
            else if (SubscriptionChannel.Reader.Completion.IsCompleted)
            {
                SubscriptionChannel = CreateChannel();
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task UnsubscribeFromAllResources()
    {
        foreach (var (client, uris) in _subscribedResources)
        {
            foreach (var uri in uris)
            {
                await client.UnsubscribeFromResourceAsync(uri, cancellationToken: CancellationToken.None);
            }
        }
    }
}