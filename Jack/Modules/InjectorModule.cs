using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Jack.App;
using Jack.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
                        DownloaderPrompt.AgentDescription))
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
                ChatInterface.Cli => services.AddSingleton<IChatMessengerClient, CliChatMessengerClient>(_ =>
                    new CliChatMessengerClient("Jack")),
                ChatInterface.Telegram =>
                    services.AddSingleton<IChatMessengerClient, TelegramBotChatMessengerClient>(_ =>
                    {
                        var botClient = new TelegramBotClient(settings.Telegram.BotToken);
                        return new TelegramBotChatMessengerClient(botClient, settings.Telegram.AllowedUserNames);
                    }),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(cmdParams.ChatInterface), "Unsupported chat interface")
            };
        }

        private IServiceCollection AddOpenRouterAdapter(AgentSettings settings)
        {
            return services.AddSingleton<IChatClient>(_ =>
                new OpenAiClient(settings.OpenRouter.ApiUrl, settings.OpenRouter.ApiKey, settings.OpenRouter.Models));
        }
    }
}