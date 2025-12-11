using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

internal sealed record ResourceProcessorConfig(
    AIAgent Agent,
    AgentThread Thread,
    string? Instructions,
    IReadOnlyList<AITool> Tools);

internal sealed class ResourceUpdateProcessor : IDisposable
{
    private readonly ResourceProcessorConfig _config;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private bool _isDisposed;

    public Channel<AgentRunResponseUpdate> OutputChannel { get; private set; } = CreateChannel();

    public ResourceUpdateProcessor(ResourceProcessorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public async Task HandleResourceUpdatedAsync(
        McpClient client,
        JsonRpcNotification notification,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var uri = notification.Params
            .Deserialize<Dictionary<string, string>>()?
            .GetValueOrDefault("uri");

        if (uri is null || OutputChannel.Reader.Completion.IsCompleted)
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

        await _syncLock.WaitAsync(ct);
        try
        {
            await foreach (var update in _config.Agent.RunStreamingAsync([message], _config.Thread, options, ct))
            {
                OutputChannel.Writer.TryWrite(update);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task HandleResourcesSyncedAsync(bool hasAnyResources, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            if (!hasAnyResources)
            {
                OutputChannel.Writer.TryComplete();
            }
            else if (OutputChannel.Reader.Completion.IsCompleted)
            {
                OutputChannel = CreateChannel();
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task EnsureChannelActive(CancellationToken ct)
    {
        if (!OutputChannel.Reader.Completion.IsCompleted)
        {
            return;
        }

        await _syncLock.WaitAsync(ct);
        try
        {
            if (OutputChannel.Reader.Completion.IsCompleted)
            {
                OutputChannel = CreateChannel();
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        OutputChannel.Writer.TryComplete();
        _syncLock.Dispose();
    }

    private static Channel<AgentRunResponseUpdate> CreateChannel()
    {
        return Channel.CreateBounded<AgentRunResponseUpdate>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }
}