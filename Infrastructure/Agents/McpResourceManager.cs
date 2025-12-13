using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed class McpResourceManager : IAsyncDisposable
{
    private readonly McpSubscriptionManager _subscriptionManager;
    private readonly ResourceUpdateProcessor _updateProcessor;
    private bool _isDisposed;

    public ChannelReader<AgentRunResponseUpdate> SubscriptionChannel => _updateProcessor.Reader;

    public McpResourceManager(AIAgent agent, AgentThread thread, string? instructions, IReadOnlyList<AITool> tools)
    {
        var config = new ResourceProcessorConfig(agent, thread, instructions, tools);
        _subscriptionManager = new McpSubscriptionManager();
        _updateProcessor = new ResourceUpdateProcessor(config);

        _subscriptionManager.ResourceUpdated += _updateProcessor.HandleResourceUpdatedAsync;
        _subscriptionManager.ResourcesSynced += _updateProcessor.HandleResourcesSyncedAsync;
    }

    public void SubscribeToNotifications(IEnumerable<McpClient> clients)
    {
        _subscriptionManager.SubscribeToNotifications(clients);
    }

    public Task SyncResourcesAsync(IEnumerable<McpClient> clients, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return _subscriptionManager.SyncResourcesAsync(clients, ct);
    }

    public Task EnsureChannelActive(CancellationToken ct)
    {
        return _updateProcessor.EnsureChannelActive(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await _subscriptionManager.DisposeAsync();
        _updateProcessor.Dispose();
    }
}