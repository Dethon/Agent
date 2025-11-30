using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Infrastructure.Storage;
using Jack.App;
using Jack.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
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
                    new DownloadAgentFactory(
                        sp.GetRequiredService<OpenAiClient>(),
                        mcpEndpoints,
                        sp.GetRequiredService<IConversationHistoryStore>()))
                .AddSingleton<AgentResolver>()
                .AddConversationHistoryStore(settings)
                .AddOpenRouterAdapter(settings);
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            services = services
                .AddSingleton<TaskQueue>(_ => new TaskQueue(cmdParams.WorkersCount * 2))
                .AddSingleton<ChatMonitor>()
                .AddSingleton<AgentCleanupMonitor>()
                .AddHostedService<ChatMonitoring>()
                .AddHostedService<CleanupMonitoring>()
                .AddWorkers(cmdParams);

            return cmdParams.ChatInterface switch
            {
                ChatInterface.Cli =>
                    services.AddSingleton<IChatMessengerClient>(_ => new CliChatMessengerClient("Jack")),
                ChatInterface.Telegram => services.AddSingleton<IChatMessengerClient>(_ =>
                {
                    var botClient = new TelegramBotClient(settings.Telegram.BotToken);
                    return new TelegramBotChatMessengerClient(botClient, settings.Telegram.AllowedUserNames);
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(cmdParams.ChatInterface),
                    "Unsupported chat interface")
            };
        }

        private IServiceCollection AddWorkers(CommandLineParams cmdParams)
        {
            for (var i = 0; i < cmdParams.WorkersCount; i++)
            {
                services.AddSingleton<IHostedService, TaskRunner>();
            }

            return services;
        }

        private IServiceCollection AddOpenRouterAdapter(AgentSettings settings)
        {
            return services.AddSingleton<OpenAiClient>(_ =>
                new OpenAiClient(settings.OpenRouter.ApiUrl, settings.OpenRouter.ApiKey, settings.OpenRouter.Models));
        }

        private IServiceCollection AddConversationHistoryStore(AgentSettings settings)
        {
            return services
                .AddSingleton<IConnectionMultiplexer>(_ =>
                    ConnectionMultiplexer.Connect(settings.Redis.ConnectionString))
                .AddSingleton<IConversationHistoryStore>(sp =>
                    new RedisConversationHistoryStore(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        TimeSpan.FromDays(settings.Redis.ConversationExpiryDays)));
        }
    }
}