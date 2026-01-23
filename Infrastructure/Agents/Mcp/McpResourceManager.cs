using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpResourceManager : IAsyncDisposable
{
    private readonly McpSubscriptionManager _subscriptionManager;
    private readonly ResourceUpdateProcessor _updateProcessor;
    private bool _isDisposed;

    public McpResourceManager(AIAgent agent, AgentThread thread, string? instructions, IReadOnlyList<AITool> tools)
    {
        var config = new ResourceProcessorConfig(agent, thread, instructions, tools);
        _updateProcessor = new ResourceUpdateProcessor(config);
        _subscriptionManager = new McpSubscriptionManager(_updateProcessor);
    }

    public Channel<AgentResponseUpdate> SubscriptionChannel => _updateProcessor.SubscriptionChannel;

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
}