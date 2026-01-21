using Infrastructure.Agents.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    McpResourceManager ResourceManager);

internal sealed class ThreadSession : IAsyncDisposable
{
    private readonly ThreadSessionData _data;
    private int _isDisposed;

    private ThreadSession(ThreadSessionData data)
    {
        _data = data;
    }

    public McpClientManager ClientManager => _data.ClientManager;
    public McpResourceManager ResourceManager => _data.ResourceManager;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        await _data.ResourceManager.DisposeAsync();
        await _data.ClientManager.DisposeAsync();
    }

    public static async Task<ThreadSession> CreateAsync(
        string[] endpoints,
        string name,
        string userId,
        string description,
        ChatClientAgent agent,
        AgentThread thread,
        CancellationToken ct)
    {
        var builder = new ThreadSessionBuilder(endpoints, name, description, agent, thread, userId);
        var data = await builder.BuildAsync(ct);
        return new ThreadSession(data);
    }
}

internal sealed class ThreadSessionBuilder(
    string[] endpoints,
    string name,
    string description,
    ChatClientAgent agent,
    AgentThread thread,
    string userId)
{
    private IReadOnlyList<AITool> _tools = [];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct)
    {
        // Step 1: Create sampling handler with deferred tool access
        var samplingHandler = new McpSamplingHandler(agent, () => _tools);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

        // Step 2: Create MCP clients and load tools/prompts
        var clientManager = await McpClientManager.CreateAsync(name, userId, description, endpoints, handlers, ct);
        _tools = clientManager.Tools;

        // Step 3: Setup resource management with user context prepended
        var resourceManager = await CreateResourceManagerAsync(clientManager, ct);

        return new ThreadSessionData(clientManager, resourceManager);
    }

    private async Task<McpResourceManager> CreateResourceManagerAsync(
        McpClientManager clientManager,
        CancellationToken ct)
    {
        var instructions = string.Join("\n\n", clientManager.Prompts);
        var resourceManager = new McpResourceManager(agent, thread, instructions, clientManager.Tools);

        await resourceManager.SyncResourcesAsync(clientManager.Clients, ct);
        resourceManager.SubscribeToNotifications(clientManager.Clients);

        return resourceManager;
    }
}