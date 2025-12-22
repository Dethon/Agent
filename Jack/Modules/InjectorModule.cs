using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Clients;
using Infrastructure.Clients.Cli;
using Infrastructure.StateManagers;
using Jack.App;
using Jack.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
                    new McpAgentFactory(
                        sp.GetRequiredService<IChatClient>(),
                        mcpEndpoints,
                        DownloaderPrompt.AgentName,
                        DownloaderPrompt.AgentDescription,
                        sp.GetRequiredService<IToolApprovalHandlerFactory>(),
                        settings.WhitelistPatterns))
                .AddRedis(settings.Redis)
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
                ChatInterface.Cli => services.AddCliClient(),
                ChatInterface.Telegram => services.AddTelegramClient(settings),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(cmdParams.ChatInterface), "Unsupported chat interface")
            };
        }

        private IServiceCollection AddRedis(RedisConfiguration config)
        {
            return services
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config.ConnectionString))
                .AddSingleton<IThreadStateStore, RedisThreadStateStore>();
        }

        private IServiceCollection AddCliClient()
        {
            var terminalAdapter = new TerminalGuiAdapter("Jack");
            var approvalHandler = new CliToolApprovalHandler(terminalAdapter);

            return services
                .AddSingleton<IToolApprovalHandlerFactory>(new CliToolApprovalHandlerFactory(approvalHandler))
                .AddSingleton<IChatMessengerClient>(sp =>
                {
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new CliChatMessengerClient(
                        "Jack",
                        Environment.UserName,
                        terminalAdapter,
                        lifetime.StopApplication);
                });
        }

        private IServiceCollection AddTelegramClient(AgentSettings settings)
        {
            var botClient = new TelegramBotClient(settings.Telegram.BotToken);

            return services
                .AddSingleton<IToolApprovalHandlerFactory>(new TelegramToolApprovalHandlerFactory(botClient))
                .AddSingleton<IChatMessengerClient>(sp => new TelegramBotChatMessengerClient(
                    botClient,
                    settings.Telegram.AllowedUserNames,
                    sp.GetRequiredService<ILogger<TelegramBotChatMessengerClient>>()));
        }

        private IServiceCollection AddOpenRouterAdapter(AgentSettings settings)
        {
            return services.AddSingleton<IChatClient>(_ => new OpenAiClient(
                settings.OpenRouter.ApiUrl,
                settings.OpenRouter.ApiKey,
                settings.OpenRouter.Models,
                false));
        }
    }
}