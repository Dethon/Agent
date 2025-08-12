using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Clients;
using Infrastructure.LLMAdapters;
using Jack.App;
using Jack.Settings;
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
                new DownloadAgentFactory(sp.GetRequiredService<OpenAiClient>(), mcpEndpoints))
            .AddSingleton<AgentResolver>()
            .AddOpenRouterAdapter(settings);
    }

    public static IServiceCollection AddChatMonitoring(
        this IServiceCollection services, AgentSettings settings, CommandLineParams cmdParams)
    {
        return services
            .AddSingleton<TaskQueue>(_ => new TaskQueue(cmdParams.WorkersCount * 2))
            .AddSingleton<ChatMonitor>()
            .AddSingleton<AgentCleanupMonitor>()
            .AddSingleton<IChatMessengerClient, TelegramBotChatMessengerClient>(_ =>
                new TelegramBotChatMessengerClient(
                    new TelegramBotClient(settings.Telegram.BotToken),
                    settings.Telegram.AllowedUserNames))
            .AddHostedService<ChatMonitoring>()
            .AddHostedService<CleanupMonitoring>()
            .AddWorkers(cmdParams);
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
        return services.AddSingleton<OpenAiClient>(_ =>
            new OpenAiClient(settings.OpenRouter.ApiUrl, settings.OpenRouter.ApiKey, settings.OpenRouter.Models));
    }
}