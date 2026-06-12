using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    IReadOnlyList<AITool> Tools,
    IVirtualFileSystemRegistry? FileSystemRegistry,
    IReadOnlyList<string> FileSystemPrompts);

internal sealed class ThreadSession : IAsyncDisposable
{
    private readonly ThreadSessionData _data;
    private int _isDisposed;

    public IReadOnlyList<AITool> Tools => _data.Tools;
    public McpClientManager ClientManager => _data.ClientManager;
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
        IReadOnlyList<AIFunction> domainTools,
        IReadOnlySet<string> filesystemEnabledTools,
        ILoggerFactory? loggerFactory,
        CancellationToken ct,
        McpPromptCache? promptCache = null)
    {
        var builder = new ThreadSessionBuilder(endpoints, name, description,
            agent, userId, domainTools, filesystemEnabledTools, loggerFactory, promptCache);
        var data = await builder.BuildAsync(ct);
        return new ThreadSession(data);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        await _data.ClientManager.DisposeAsync();
    }
}

internal sealed class ThreadSessionBuilder(
    string[] endpoints,
    string name,
    string description,
    ChatClientAgent agent,
    string userId,
    IReadOnlyList<AIFunction> domainTools,
    IReadOnlySet<string> filesystemEnabledTools,
    ILoggerFactory? loggerFactory,
    McpPromptCache? promptCache = null)
{
    private static readonly HashSet<string> _fileSystemMcpToolNames =
    [
        "fs_read", "fs_create", "fs_edit", "fs_glob", "fs_search", "fs_move", "fs_delete", "fs_exec",
        "fs_copy", "fs_info", "fs_blob_read", "fs_blob_write"
    ];

    // Channel-protocol tools are invoked directly by the channel connection layer, never by the LLM.
    // A dual-role server (e.g. mcp-scheduling, which is both a channel and a filesystem tool server)
    // exposes them on the same /mcp endpoint, so they leak into the agent-visible tool list unless stripped.
    private static readonly HashSet<string> _channelProtocolToolNames =
    [
        ChannelProtocol.SendReplyTool,
        ChannelProtocol.RequestApprovalTool,
        ChannelProtocol.CreateConversationTool,
        ChannelProtocol.RegisterAgentsTool
    ];

    private IReadOnlyList<AITool> _tools = [];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct)
    {
        // Step 1: Create sampling handler with deferred tool access
        var samplingHandler = new McpSamplingHandler(agent, () => _tools);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

        // Step 2: Create MCP clients and load tools/prompts
        var clientManager = await McpClientManager.CreateAsync(name, userId, description, endpoints, handlers, promptCache, ct);

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

        // Step 4: Combine MCP tools with domain tools and filesystem tools.
        // Channel-protocol tools are always stripped; raw fs_* tools are stripped when their
        // domain filesystem wrappers are active, to avoid exposing duplicate functionality to the LLM.
        var mcpTools = FilterMcpTools(clientManager.Tools, fileSystemTools.Count > 0);
        _tools = mcpTools.Concat(domainTools).Concat(fileSystemTools).ToList();

        return new ThreadSessionData(clientManager, _tools, registry, fileSystemPrompts);
    }

    internal static IReadOnlyList<AITool> FilterMcpTools(IReadOnlyList<AITool> mcpTools, bool filesystemToolsActive)
    {
        return mcpTools
            .Where(t => !HasReservedSuffix(t.Name, _channelProtocolToolNames))
            .Where(t => !filesystemToolsActive || !HasReservedSuffix(t.Name, _fileSystemMcpToolNames))
            .ToList();
    }

    private static bool HasReservedSuffix(string toolName, HashSet<string> reserved)
    {
        return reserved.Any(n => toolName.EndsWith($"__{n}", StringComparison.Ordinal));
    }
}