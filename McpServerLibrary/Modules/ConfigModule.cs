using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using McpServerLibrary.McpPrompts;
using McpServerLibrary.McpResources;
using McpServerLibrary.McpTools;
using McpServerLibrary.Services;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace McpServerLibrary.Modules;

public static class ConfigModule
{
    public static McpSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<McpSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        var emitter = new DownloadNotificationEmitter(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<DownloadNotificationEmitter>());

        services
            .AddMemoryCache()
            .AddSingleton(settings)
            .AddSingleton(emitter)
            .AddSingleton<IDownloadNotificationEmitter>(emitter)
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.BaseLibraryPath))
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
            .AddSingleton<IDownloadRoutingStore, RedisDownloadRoutingStore>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient()
            .AddSingleton<DownloadsOverlay>()
            .AddHostedService<DownloadCompletionWatcher>()
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
#pragma warning disable MCPEXP002 // RunSessionHandler is experimental
                options.RunSessionHandler = async (_, server, ct) =>
                {
                    var sessionId = server.SessionId ?? Guid.NewGuid().ToString();
                    emitter.RegisterSession(sessionId, server);
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        emitter.UnregisterSession(sessionId);
                    }
                };
#pragma warning restore MCPEXP002
            })
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return ToolResponse.Create(ex);
                }
            }))
            // Download tools
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .WithTools<McpContentRecommendationTool>()
            // Channel-protocol tools (invoked by the agent's channel connection, hidden from the LLM)
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            // Filesystem backend tools
            .WithTools<FsGlobTool>()
            .WithTools<FsReadTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsInfoTool>()
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
            // Prompts
            .WithPrompts<McpSystemPrompt>()
            // Resources
            .WithResources<FileSystemResource>();

        return services;
    }
}