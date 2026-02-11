using Agent.App;
using Agent.Hubs;
using Agent.Settings;
using Azure.Messaging.ServiceBus;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Domain.Routers;
using Infrastructure.Agents;
using Infrastructure.Clients.Messaging;
using Infrastructure.Clients.Messaging.Cli;
using Infrastructure.Clients.Messaging.ServiceBus;
using Infrastructure.Clients.Messaging.Telegram;
using Infrastructure.Clients.Messaging.WebChat;
using Infrastructure.Clients.ToolApproval;
using Infrastructure.CliGui.Routing;
using Infrastructure.CliGui.Ui;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Telegram.Bot;
using HubNotifier = Infrastructure.Clients.Messaging.WebChat.HubNotifier;

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
                .AddSingleton<IDomainToolRegistry, DomainToolRegistry>()
                .AddSingleton<IAgentFactory>(sp =>
                    new MultiAgentFactory(
                        sp,
                        sp.GetRequiredService<IOptionsMonitor<AgentRegistryOptions>>(),
                        llmConfig,
                        sp.GetRequiredService<IDomainToolRegistry>()))
                .AddSingleton<IScheduleAgentFactory>(sp =>
                    (IScheduleAgentFactory)sp.GetRequiredService<IAgentFactory>());
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            if (cmdParams.ChatInterface == ChatInterface.Web)
            {
                services = services.AddSignalR(options =>
                {
                    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
                }).Services;
            }

            services = services
                .AddSingleton<ChatMonitor>()
                .AddHostedService<ChatMonitoring>();

            return cmdParams.ChatInterface switch
            {
                ChatInterface.Cli => services.AddCliClient(settings, cmdParams),
                ChatInterface.Telegram => services.AddTelegramClient(settings, cmdParams),
                ChatInterface.OneShot => services.AddOneShotClient(cmdParams),
                ChatInterface.Web => services.AddWebClient(settings),
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
                    TimeSpan.FromDays(config.ExpirationDays ?? 30)))
                .AddSingleton<IPushSubscriptionStore>(sp => new RedisPushSubscriptionStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()));
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
            var agentBots = settings.Agents
                .Where(a => a.TelegramBotToken is not null)
                .Select(a => (a.Id, a.TelegramBotToken!))
                .ToArray();

            if (agentBots.Length == 0)
            {
                throw new InvalidOperationException("No Telegram bot tokens configured in agents.");
            }

            var botClientsByAgentId = agentBots.ToDictionary(
                ab => ab.Id, ITelegramBotClient (ab) => TelegramBotHelper.CreateBotClient(ab.Item2));

            return services
                .AddSingleton<IToolApprovalHandlerFactory>(new TelegramToolApprovalHandlerFactory(botClientsByAgentId))
                .AddSingleton<IChatMessengerClient>(sp => new TelegramChatClient(
                    agentBots,
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

        private IServiceCollection AddWebClient(AgentSettings settings)
        {
            services = services
                .AddSingleton<IHubNotificationSender, HubNotificationAdapter>()
                .AddSingleton<INotifier, HubNotifier>()
                .AddSingleton<IPushNotificationService, NullPushNotificationService>()
                .AddSingleton<WebChatSessionManager>()
                .AddSingleton<WebChatStreamManager>()
                .AddSingleton<WebChatApprovalManager>()
                .AddSingleton<WebChatMessengerClient>()
                .AddSingleton<IToolApprovalHandlerFactory>(sp =>
                    new WebToolApprovalHandlerFactory(
                        sp.GetRequiredService<WebChatApprovalManager>(),
                        sp.GetRequiredService<WebChatSessionManager>()));

            if (settings.ServiceBus is not null)
            {
                return services.AddServiceBusClient(settings.ServiceBus, settings.Agents);
            }

            return services
                .AddSingleton<IChatMessengerClient>(sp => sp.GetRequiredService<WebChatMessengerClient>());
        }

        private IServiceCollection AddServiceBusClient(ServiceBusSettings sbSettings,
            IReadOnlyList<AgentDefinition> agents)
        {
            var validAgentIds = agents.Select(a => a.Id).ToList();

            return services
                .AddSingleton(_ => new ServiceBusClient(sbSettings.ConnectionString))
                .AddSingleton(sp =>
                {
                    var client = sp.GetRequiredService<ServiceBusClient>();
                    return client.CreateProcessor(sbSettings.PromptQueueName, new ServiceBusProcessorOptions
                    {
                        AutoCompleteMessages = false,
                        MaxConcurrentCalls = sbSettings.MaxConcurrentCalls
                    });
                })
                .AddSingleton(sp =>
                {
                    var client = sp.GetRequiredService<ServiceBusClient>();
                    return client.CreateSender(sbSettings.ResponseQueueName);
                })
                .AddSingleton<ServiceBusConversationMapper>()
                .AddSingleton(_ => new ServiceBusMessageParser(validAgentIds))
                .AddSingleton(sp => new ServiceBusPromptReceiver(
                    sp.GetRequiredService<ServiceBusConversationMapper>(),
                    sp.GetRequiredService<INotifier>(),
                    sp.GetRequiredService<ILogger<ServiceBusPromptReceiver>>()))
                .AddSingleton(sp => new ServiceBusResponseWriter(
                    sp.GetRequiredService<ServiceBusSender>(),
                    sp.GetRequiredService<ILogger<ServiceBusResponseWriter>>()))
                .AddSingleton(sp => new ServiceBusResponseHandler(
                    sp.GetRequiredService<ServiceBusPromptReceiver>(),
                    sp.GetRequiredService<ServiceBusResponseWriter>()))
                .AddSingleton(sp => new ServiceBusChatMessengerClient(
                    sp.GetRequiredService<ServiceBusPromptReceiver>(),
                    sp.GetRequiredService<ServiceBusResponseHandler>()))
                .AddSingleton<IMessageSourceRouter, MessageSourceRouter>()
                .AddSingleton<IChatMessengerClient>(sp => new CompositeChatMessengerClient(
                    [
                        sp.GetRequiredService<WebChatMessengerClient>(),
                        sp.GetRequiredService<ServiceBusChatMessengerClient>()
                    ],
                    sp.GetRequiredService<IMessageSourceRouter>()))
                .AddSingleton(sp => new ServiceBusProcessorHost(
                    sp.GetRequiredService<ServiceBusProcessor>(),
                    sp.GetRequiredService<ServiceBusMessageParser>(),
                    sp.GetRequiredService<ServiceBusPromptReceiver>(),
                    sp.GetRequiredService<ILogger<ServiceBusProcessorHost>>()))
                .AddHostedService(sp => sp.GetRequiredService<ServiceBusProcessorHost>());
        }
    }
}