using Microsoft.Agents.AI;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed class ThreadSession : IAsyncDisposable
{
    public McpClientManager ClientManager { get; }
    public McpResourceManager ResourceManager { get; }

    private readonly McpSamplingHandler _samplingHandler;

    private ThreadSession(
        McpClientManager clientManager,
        McpResourceManager resourceManager,
        McpSamplingHandler samplingHandler)
    {
        ClientManager = clientManager;
        ResourceManager = resourceManager;
        _samplingHandler = samplingHandler;
    }

    public static async Task<ThreadSession> CreateAsync(
        string[] endpoints,
        string name,
        string description,
        ChatClientAgent agent,
        AgentThread thread,
        CancellationToken ct)
    {
        var samplingHandler = new McpSamplingHandler(agent);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

        var clientManager = await McpClientManager.CreateAsync(name, description, endpoints, handlers, ct);
        samplingHandler.SetTools(clientManager.Tools);

        var instructions = string.Join("\n\n", clientManager.Prompts);
        var resourceManager = new McpResourceManager(agent, thread, instructions, clientManager.Tools);
        await resourceManager.SyncResourcesAsync(clientManager.Clients, ct);
        resourceManager.SubscribeToNotifications(clientManager.Clients);

        return new ThreadSession(clientManager, resourceManager, samplingHandler);
    }

    public async ValueTask DisposeAsync()
    {
        _samplingHandler.Dispose();
        await ResourceManager.DisposeAsync();
        await ClientManager.DisposeAsync();
    }
}