using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    McpResourceManager? ResourceManager,
    IReadOnlyList<AITool> Tools,
    IVirtualFileSystemRegistry? FileSystemRegistry,
    IReadOnlyList<string> FileSystemPrompts);

internal sealed class ThreadSession : IAsyncDisposable
{
    private readonly ThreadSessionData _data;
    private int _isDisposed;

    public IReadOnlyList<AITool> Tools => _data.Tools;
    public McpClientManager ClientManager => _data.ClientManager;
    public McpResourceManager? ResourceManager => _data.ResourceManager;
    public IReadOnlyList<string> FileSystemPrompts => _data.FileSystemPrompts;

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
        IReadOnlySet<string> filesystemEnabledTools,
        ILoggerFactory? loggerFactory,
        CancellationToken ct,
        bool enableResourceSubscriptions = true)
    {
        var builder = new ThreadSessionBuilder(endpoints, name, description,
            agent, thread, userId, domainTools, filesystemEnabledTools, loggerFactory);
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
    IReadOnlyList<AIFunction> domainTools,
    IReadOnlySet<string> filesystemEnabledTools,
    ILoggerFactory? loggerFactory)
{
    private static readonly HashSet<string> _fileSystemMcpToolNames =
    [
        "fs_read", "fs_create", "fs_edit", "fs_glob", "fs_search", "fs_move", "fs_delete", "fs_exec",
        "fs_copy", "fs_info", "fs_blob_read", "fs_blob_write"
    ];

    private IReadOnlyList<AITool> _tools = [];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct, bool enableResourceSubscriptions = true)
    {
        // Step 1: Create sampling handler with deferred tool access
        var samplingHandler = new McpSamplingHandler(agent, () => _tools);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

        // Step 2: Create MCP clients and load tools/prompts
        var clientManager = await McpClientManager.CreateAsync(name, userId, description, endpoints, handlers, ct);

        // Step 3: Discover filesystem backends from connected MCP clients
        IVirtualFileSystemRegistry? registry = null;
        IReadOnlyList<AIFunction> fileSystemTools = [];
        IReadOnlyList<string> fileSystemPrompts = [];
        var fsLogger = loggerFactory?.CreateLogger(typeof(McpFileSystemDiscovery).FullName!)
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var fsRegistry = new VirtualFileSystemRegistry();
        await McpFileSystemDiscovery.DiscoverAndMountAsync(clientManager.Clients, fsRegistry, fsLogger, ct);

        if (fsRegistry.GetMounts().Count > 0)
        {
            if (filesystemEnabledTools.Count == 0)
            {
                var mountNames = string.Join(", ", fsRegistry.GetMounts().Select(m => m.Name));
                fsLogger.LogDebug(
                    "MCP servers expose filesystem resources ({Mounts}) but the 'filesystem' feature is not enabled for this agent. " +
                    "Add 'filesystem' to enabledFeatures to use virtual filesystem tools",
                    mountNames);
            }
            else
            {
                registry = fsRegistry;
                var fsFeatureConfig = new FeatureConfig(EnabledTools: filesystemEnabledTools);
                var feature = new FileSystemToolFeature(registry);
                fileSystemTools = feature.GetTools(fsFeatureConfig).ToList();
                fileSystemPrompts = feature.Prompt is not null ? [feature.Prompt] : [];
            }
        }

        // Step 4: Combine MCP tools with domain tools and filesystem tools
        // When filesystem domain tools are active, filter out the raw MCP fs_* tools
        // they wrap to avoid exposing duplicate functionality to the LLM
        var mcpTools = fileSystemTools.Count > 0
            ? clientManager.Tools.Where(t => !_fileSystemMcpToolNames.Any(n => t.Name.EndsWith($"__{n}")))
            : clientManager.Tools;
        _tools = mcpTools.Concat(domainTools).Concat(fileSystemTools).ToList();

        // Step 5: Setup resource management with user context prepended (skipped for subagents)
        McpResourceManager? resourceManager = enableResourceSubscriptions
            ? await CreateResourceManagerAsync(clientManager, ct)
            : null;

        return new ThreadSessionData(clientManager, resourceManager, _tools, registry, fileSystemPrompts);
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
