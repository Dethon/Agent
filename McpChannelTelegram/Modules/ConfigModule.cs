using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;

namespace McpChannelTelegram.Modules;

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
            .AddSingleton(new BotRegistry(settings.Bots))
            .AddSingleton<MessageAccumulator>()
            .AddSingleton<ApprovalCallbackRouter>()
            .AddHostedService<TelegramBotService>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            // Buffer-always: Telegram has no channel-level way to tell a sender "try again later",
            // so a message arriving during a cold start or just after an idle eviction must be
            // buffered rather than fanned out to nobody. The target is the id McpChannelConnection
            // derives for itself; a mismatch would buffer into a queue nobody drains.
            .AddChannelServer(
                DeliveryPolicy.BufferAlways,
                ChannelProtocol.ChannelClientNamePrefix + "telegram");

        return services;
    }
}