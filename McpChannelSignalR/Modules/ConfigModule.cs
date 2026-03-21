using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace McpChannelSignalR.Modules;

public static class ConfigModule
{
    public static ChannelSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<ChannelSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureChannel(this IServiceCollection services, ChannelSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddSingleton<IStreamService, StubStreamService>()
            .AddSingleton<IApprovalService, StubApprovalService>()
            .AddSingleton<ISessionService, StubSessionService>()
            .AddSignalR();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<CreateConversationTool>()
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
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = ex.Message }]
                    };
                }
            }));

        return services;
    }
}
