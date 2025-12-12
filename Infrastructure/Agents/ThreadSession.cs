using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    McpResourceManager ResourceManager,
    McpSamplingHandler SamplingHandler);

internal sealed class ThreadSession : IAsyncDisposable
{
    private readonly ThreadSessionData _data;
    private int _isDisposed;

    public McpClientManager ClientManager => _data.ClientManager;
    public McpResourceManager ResourceManager => _data.ResourceManager;

    private ThreadSession(ThreadSessionData data)
    {
        _data = data;
    }

    ~ThreadSession()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
        {
            _ = Task.Run(async () => await DisposeAsyncCore());
        }
    }

    public static async Task<ThreadSession> CreateAsync(
        string[] endpoints,
        string name,
        string description,
        ChatClientAgent agent,
        AgentThread thread,
        CancellationToken ct)
    {
        var builder = new ThreadSessionBuilder(endpoints, name, description, agent, thread);
        var data = await builder.BuildAsync(ct);
        return new ThreadSession(data);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        await DisposeAsyncCore();
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeAsyncCore()
    {
        _data.SamplingHandler.Dispose();
        await _data.ResourceManager.DisposeAsync();
        await _data.ClientManager.DisposeAsync();
    }
}

internal sealed class ThreadSessionBuilder(
    string[] endpoints,
    string name,
    string description,
    ChatClientAgent agent,
    AgentThread thread)
{
    private IReadOnlyList<AITool> _tools = [];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct)
    {
        // Step 1: Create sampling handler with deferred tool access
        var samplingHandler = new McpSamplingHandler(agent, () => _tools);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

        // Step 2: Create MCP clients and load tools/prompts
        var clientManager = await McpClientManager.CreateAsync(name, description, endpoints, handlers, ct);
        _tools = clientManager.Tools;

        // Step 3: Setup resource management
        var resourceManager = await CreateResourceManagerAsync(clientManager, ct);

        return new ThreadSessionData(clientManager, resourceManager, samplingHandler);
    }

    private async Task<McpResourceManager> CreateResourceManagerAsync(
        McpClientManager clientManager,
        CancellationToken ct)
    {
        var instructions = string.Join("\n\n", clientManager.Prompts);
        var resourceManager = McpResourceManager.Create(agent, thread, instructions, clientManager.Tools);

        await resourceManager.SyncResourcesAsync(clientManager.Clients, ct);
        resourceManager.SubscribeToNotifications(clientManager.Clients);

        return resourceManager;
    }
}