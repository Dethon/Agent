using Infrastructure.Agents.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    McpResourceManager? ResourceManager,
    IReadOnlyList<AITool> Tools);

internal sealed class ThreadSession : IAsyncDisposable
{
    private readonly ThreadSessionData _data;
    private int _isDisposed;

    public IReadOnlyList<AITool> Tools => _data.Tools;
    public McpClientManager ClientManager => _data.ClientManager;
    public McpResourceManager? ResourceManager => _data.ResourceManager;

    private ThreadSession(ThreadSessionData data)
    {
        _data = data;
    }

    public static async Task<ThreadSession> CreateAsync(
        string[] endpoints,
        string name,
        string userId,
        string description,
        ChatClientAgent agent,
        AgentSession thread,
        IReadOnlyList<AIFunction> domainTools,
        CancellationToken ct,
        bool enableResourceSubscriptions = true)
    {
        var builder = new ThreadSessionBuilder(endpoints, name, description, agent, thread, userId, domainTools);
        var data = await builder.BuildAsync(ct, enableResourceSubscriptions);
        return new ThreadSession(data);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        if (_data.ResourceManager is not null)
        {
            await _data.ResourceManager.DisposeAsync();
        }

        await _data.ClientManager.DisposeAsync();
    }
}

internal sealed class ThreadSessionBuilder(
    string[] endpoints,
    string name,
    string description,
    ChatClientAgent agent,
    AgentSession thread,
    string userId,
    IReadOnlyList<AIFunction> domainTools)
{
    private IReadOnlyList<AITool> _tools = [];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct, bool enableResourceSubscriptions = true)
    {
        // Step 1: Create sampling handler with deferred tool access
        var samplingHandler = new McpSamplingHandler(agent, () => _tools);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

        // Step 2: Create MCP clients and load tools/prompts
        var clientManager = await McpClientManager.CreateAsync(name, userId, description, endpoints, handlers, ct);

        // Step 3: Combine MCP tools with domain tools
        _tools = clientManager.Tools.Concat(domainTools).ToList();

        // Step 4: Setup resource management with user context prepended (skipped for subagents)
        McpResourceManager? resourceManager = enableResourceSubscriptions
            ? await CreateResourceManagerAsync(clientManager, ct)
            : null;

        return new ThreadSessionData(clientManager, resourceManager, _tools);
    }

    private async Task<McpResourceManager> CreateResourceManagerAsync(
        McpClientManager clientManager,
        CancellationToken ct)
    {
        var instructions = string.Join("\n\n", clientManager.Prompts);
        var resourceManager = new McpResourceManager(agent, thread, instructions, _tools);

        await resourceManager.SyncResourcesAsync(clientManager.Clients, ct);
        resourceManager.SubscribeToNotifications(clientManager.Clients);

        return resourceManager;
    }
}