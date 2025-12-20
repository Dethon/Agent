using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Jack.App;
using Jack.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace Jack.Modules;

public static class InjectorModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAgent(AgentSettings settings)
        {
            var mcpEndpoints = settings.McpServers.Select(x => x.Endpoint).ToArray();
            return services
                .AddSingleton<IAgentFactory>(sp =>
                    new McpAgentFactory(
                        sp.GetRequiredService<IChatClient>(),
                        mcpEndpoints,
                        DownloaderPrompt.AgentName,
                        DownloaderPrompt.AgentDescription,
                        sp.GetService<TelegramToolApprovalHandler>(),
                        settings.WhitelistedTools))
                .AddSingleton<ChatThreadResolver>()
                .AddOpenRouterAdapter(settings);
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            services = services
                .AddSingleton<ChatMonitor>()
                .AddSingleton<AgentCleanupMonitor>()
                .AddHostedService<ChatMonitoring>()
                .AddHostedService<CleanupMonitoring>();

            return cmdParams.ChatInterface switch
            {
                ChatInterface.Cli => services.AddSingleton<IChatMessengerClient, CliChatMessengerClient>(sp =>
                {
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new CliChatMessengerClient("Jack", lifetime.StopApplication);
                }),
                ChatInterface.Telegram => services.AddTelegramClient(settings),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(cmdParams.ChatInterface), "Unsupported chat interface")
            };
        }

        private IServiceCollection AddTelegramClient(AgentSettings settings)
        {
            var botClient = new TelegramBotClient(settings.Telegram.BotToken);
            var approvalHandler = new TelegramToolApprovalHandler(botClient);

            return services
                .AddSingleton(approvalHandler)
                .AddSingleton<IChatMessengerClient>(_ =>
                    new TelegramBotChatMessengerClient(botClient, settings.Telegram.AllowedUserNames, approvalHandler));
        }

        private IServiceCollection AddOpenRouterAdapter(AgentSettings settings)
        {
            return services.AddSingleton<IChatClient>(_ =>
                new OpenAiClient(settings.OpenRouter.ApiUrl, settings.OpenRouter.ApiKey, settings.OpenRouter.Models));
        }
    }
}