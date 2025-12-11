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

    public Channel<AgentRunResponseUpdate> SubscriptionChannel => _updateProcessor.OutputChannel;

    private McpResourceManager(
        McpSubscriptionManager subscriptionManager,
        ResourceUpdateProcessor updateProcessor)
    {
        _subscriptionManager = subscriptionManager;
        _updateProcessor = updateProcessor;

        _subscriptionManager.ResourceUpdated += _updateProcessor.HandleResourceUpdatedAsync;
        _subscriptionManager.ResourcesSynced += _updateProcessor.HandleResourcesSyncedAsync;
    }

    public static McpResourceManager Create(
        AIAgent agent,
        AgentThread thread,
        string? instructions,
        IReadOnlyList<AITool> tools)
    {
        var config = new ResourceProcessorConfig(agent, thread, instructions, tools);
        var subscriptionManager = new McpSubscriptionManager();
        var updateProcessor = new ResourceUpdateProcessor(config);

        return new McpResourceManager(subscriptionManager, updateProcessor);
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

    public void EnsureChannelActive()
    {
        _updateProcessor.EnsureChannelActive();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _updateProcessor.Dispose();
        await _subscriptionManager.DisposeAsync();
    }
}