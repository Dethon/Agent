using Microsoft.Agents.AI;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed class ThreadSession : IAsyncDisposable
{
    public McpClientManager ClientManager { get; }
    public McpResourceManager ResourceManager { get; }

    private readonly McpSamplingHandler _samplingHandler;

    public async ValueTask DisposeAsync()
    {
        _samplingHandler.Dispose();
        await ResourceManager.DisposeAsync();
        await ClientManager.DisposeAsync();
    }

    public static async Task<ThreadSession> CreateAsync(
        string[] endpoints,
        string name,
        string description,
        ChatClientAgent chatClient,
        AgentThread thread,
        CancellationToken ct)
    {
        var session = new ThreadSession(chatClient, thread);
        await session.Initialize(endpoints, name, description, ct);
        return session;
    }

    private ThreadSession(ChatClientAgent agent, AgentThread thread)
    {
        ClientManager = new McpClientManager();
        ResourceManager = new McpResourceManager(agent, thread);
        _samplingHandler = new McpSamplingHandler(agent, () => ClientManager.Tools);
    }

    private async Task Initialize(string[] endpoints, string name, string description, CancellationToken ct)
    {
        var handlers = new McpClientHandlers
        {
            SamplingHandler = _samplingHandler.HandleAsync
        };
        await ClientManager.InitializeAsync(name, description, endpoints, handlers, ct);
        ResourceManager.Instructions = string.Join("\n\n", ClientManager.Prompts);
        ResourceManager.Tools = ClientManager.Tools;
        await ResourceManager.SyncResourcesAsync(ClientManager.Clients, ct);
        ResourceManager.SubscribeToNotifications(ClientManager.Clients);
    }
}