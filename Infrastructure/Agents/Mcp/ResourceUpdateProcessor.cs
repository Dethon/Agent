using System.Text.Json;
using System.Threading.Channels;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

internal sealed record ResourceProcessorConfig(
    AIAgent Agent,
    AgentThread Thread,
    string? Instructions,
    IReadOnlyList<AITool> Tools);

internal sealed class ResourceUpdateProcessor : IDisposable
{
    private readonly ResourceProcessorConfig _config;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private int _isDisposed;

    public Channel<AgentRunResponseUpdate> SubscriptionChannel { get; private set; } = CreateChannel();

    public ResourceUpdateProcessor(ResourceProcessorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        SubscriptionChannel.Writer.TryComplete();
        _syncLock.Dispose();
    }

    public async Task HandleResourceUpdatedAsync(
        McpClient client,
        JsonRpcNotification notification,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        var uri = notification.Params
            .Deserialize<Dictionary<string, string>>()?
            .GetValueOrDefault("uri");

        if (uri is null)
        {
            return;
        }

        var resource = await client.ReadResourceAsync(uri, cancellationToken: ct);
        var message = new ChatMessage(ChatRole.User, resource.Contents.ToAIContents());
        var options = new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [.._config.Tools],
            Instructions = _config.Instructions
        });

        await _syncLock.WithLockAsync(async () =>
        {
            await foreach (var update in _config.Agent.RunStreamingAsync([message], _config.Thread, options, ct))
            {
                SubscriptionChannel.Writer.TryWrite(update);
            }
        }, ct);
    }

    public async Task HandleResourcesSyncedAsync(bool hasAnyResources, CancellationToken ct)
    {
        await _syncLock.WithLockAsync(() =>
        {
            if (!hasAnyResources)
            {
                SubscriptionChannel.Writer.TryComplete();
            }
            else if (SubscriptionChannel.Reader.Completion.IsCompleted)
            {
                SubscriptionChannel = CreateChannel();
            }

            return Task.CompletedTask;
        }, ct);
    }

    public async Task EnsureChannelActive(CancellationToken ct)
    {
        await _syncLock.WithLockAsync(() =>
        {
            if (SubscriptionChannel.Reader.Completion.IsCompleted)
            {
                SubscriptionChannel = CreateChannel();
            }

            return Task.CompletedTask;
        }, ct);
    }

    private static Channel<AgentRunResponseUpdate> CreateChannel()
    {
        return Channel.CreateBounded<AgentRunResponseUpdate>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }
}