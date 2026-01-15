using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Infrastructure.CliGui.Routing;
using Infrastructure.CliGui.Ui;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agent.Modules;

public static class InjectorModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAgent(AgentSettings settings)
        {
            var llmConfig = new OpenRouterConfig
            {
                ApiUrl = settings.OpenRouter.ApiUrl,
                ApiKey = settings.OpenRouter.ApiKey
            };

            services.Configure<AgentRegistryOptions>(options => options.Agents = settings.Agents);

            return services
                .AddRedis(settings.Redis)
                .AddSingleton<ChatThreadResolver>()
                .AddSingleton<IAgentFactory>(sp =>
                    new MultiAgentFactory(
                        sp,
                        sp.GetRequiredService<IOptionsMonitor<AgentRegistryOptions>>(),
                        llmConfig));
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            if (cmdParams.ChatInterface == ChatInterface.Web)
            {
                services = services.AddSignalR().Services;
            }

            services = services
                .AddSingleton<ChatMonitor>()
                .AddHostedService<ChatMonitoring>();

            return cmdParams.ChatInterface switch
            {
                ChatInterface.Cli => services.AddCliClient(settings, cmdParams),
                ChatInterface.Telegram => services.AddTelegramClient(settings, cmdParams),
                ChatInterface.OneShot => services.AddOneShotClient(cmdParams),
                ChatInterface.Web => services.AddWebClient(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(cmdParams.ChatInterface), "Unsupported chat interface")
            };
        }

        private IServiceCollection AddRedis(RedisConfiguration config)
        {
            return services
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config.ConnectionString))
                .AddSingleton<IThreadStateStore>(sp => new RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    TimeSpan.FromDays(config.ExpirationDays ?? 30))
                );
        }

        private IServiceCollection AddCliClient(AgentSettings settings, CommandLineParams cmdParams)
        {
            var agent = settings.Agents[0];
            var terminalAdapter = new TerminalGuiAdapter(agent.Name);
            var approvalHandler = new CliToolApprovalHandler(terminalAdapter);

            return services
                .AddSingleton<IToolApprovalHandlerFactory>(new CliToolApprovalHandlerFactory(approvalHandler))
                .AddSingleton<IChatMessengerClient>(sp =>
                {
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    var threadStateStore = sp.GetRequiredService<IThreadStateStore>();

                    var router = new CliChatMessageRouter(
                        agent.Name,
                        Environment.UserName,
                        terminalAdapter,
                        cmdParams.ShowReasoning);

                    return new CliChatMessengerClient(
                        router,
                        lifetime.StopApplication,
                        threadStateStore);
                });
        }

        private IServiceCollection AddTelegramClient(AgentSettings settings, CommandLineParams cmdParams)
        {
            var botTokens = settings.Agents
                .Select(a => a.TelegramBotToken)
                .Where(t => t is not null)
                .Cast<string>()
                .ToArray();

            if (botTokens.Length == 0)
            {
                throw new InvalidOperationException("No Telegram bot tokens configured in agents.");
            }

            var botClientsByHash = TelegramBotHelper.CreateBotClientsByHash(botTokens);

            return services
                .AddHostedService<CleanupMonitoring>()
                .AddSingleton<AgentCleanupMonitor>()
                .AddSingleton<IToolApprovalHandlerFactory>(new TelegramToolApprovalHandlerFactory(botClientsByHash))
                .AddSingleton<IChatMessengerClient>(sp => new TelegramChatClient(
                    botTokens,
                    settings.Telegram.AllowedUserNames,
                    cmdParams.ShowReasoning,
                    sp.GetRequiredService<ILogger<TelegramChatClient>>()));
        }

        private IServiceCollection AddOneShotClient(CommandLineParams cmdParams)
        {
            return services
                .AddSingleton<IToolApprovalHandlerFactory>(new AutoApproveToolHandlerFactory())
                .AddSingleton<IChatMessengerClient>(sp =>
                {
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new OneShotChatMessengerClient(
                        cmdParams.Prompt ?? throw new InvalidOperationException("Prompt is required for OneShot mode"),
                        cmdParams.ShowReasoning,
                        lifetime);
                });
        }

        private IServiceCollection AddWebClient()
        {
            return services
                .AddSingleton<WebChatMessengerClient>()
                .AddSingleton<IChatMessengerClient>(sp => sp.GetRequiredService<WebChatMessengerClient>())
                .AddSingleton<IToolApprovalHandlerFactory>(sp =>
                    new WebToolApprovalHandlerFactory(sp.GetRequiredService<WebChatMessengerClient>()));
        }
    }
}