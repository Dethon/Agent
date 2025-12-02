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
    public static IServiceCollection AddAgent(this IServiceCollection services, AgentSettings settings)
    {
        var mcpEndpoints = settings.McpServers.Select(x => x.Endpoint).ToArray();
        return services
            .AddSingleton<IAgentFactory>(sp =>
                new DownloadAgentFactory(sp.GetRequiredService<IChatClient>(), mcpEndpoints))
            .AddSingleton<AgentResolver>()
            .AddOpenRouterAdapter(settings);
    }

    public static IServiceCollection AddChatMonitoring(
        this IServiceCollection services, AgentSettings settings, CommandLineParams cmdParams)
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
            ChatInterface.Cli => services.AddSingleton<IChatMessengerClient>(_ => new CliChatMessengerClient("Jack")),
            ChatInterface.Telegram => services.AddSingleton<IChatMessengerClient>(_ =>
            {
                var botClient = new TelegramBotClient(settings.Telegram.BotToken);
                return new TelegramBotChatMessengerClient(botClient, settings.Telegram.AllowedUserNames);
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(cmdParams.ChatInterface), "Unsupported chat interface")
        };
    }

    private static IServiceCollection AddWorkers(this IServiceCollection services, CommandLineParams cmdParams)
    {
        for (var i = 0; i < cmdParams.WorkersCount; i++)
        {
            services.AddSingleton<IHostedService, TaskRunner>();
        }

        return services;
    }

    private static IServiceCollection AddOpenRouterAdapter(this IServiceCollection services, AgentSettings settings)
    {
        return services.AddSingleton<IChatClient>(_ =>
            new OpenAiClient(settings.OpenRouter.ApiUrl, settings.OpenRouter.ApiKey, settings.OpenRouter.Models));
    }
}