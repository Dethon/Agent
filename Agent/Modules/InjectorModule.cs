using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace Agent.Modules;

using AgentFactory = Func<Func<AiPartialResponse, CancellationToken, Task>, CancellationToken, Task<IAgent>>; 

public static class InjectorModule
{
    public static IServiceCollection AddOpenRouterAdapter(this IServiceCollection services, AgentSettings settings)
    {
        return services.AddSingleton<OpenAiClient>(_ =>
            new OpenAiClient(settings.OpenRouter.ApiUrl, settings.OpenRouter.ApiKey, settings.OpenRouter.Model));
    }

    public static IServiceCollection AddAgentFactory(this IServiceCollection services, AgentSettings settings)
    {
        return services.AddSingleton<AgentFactory>(sp =>
            (callback, ct) => Infrastructure.Agents.Agent.CreateAsync(
                settings.McpServers.Select(x => x.Endpoint).ToArray(),
                DownloaderPrompt.Get().Select(x => x.ToChatMessage()).ToArray(),
                callback,
                sp.GetRequiredService<OpenAiClient>(),
                ct));
    }

    public static IServiceCollection AddChatMonitoring(this IServiceCollection services, AgentSettings settings)
    {
        return services
            .AddSingleton<TaskQueue>()
            .AddSingleton<ChatMonitor>()
            .AddSingleton<IChatMessengerClient, TelegramBotChatMessengerClient>(_ =>
                new TelegramBotChatMessengerClient(
                    new TelegramBotClient(settings.Telegram.BotToken),
                    settings.Telegram.AllowedUserNames));
    }

    public static IServiceCollection AddWorkers(this IServiceCollection services, int amount)
    {
        for (var i = 0; i < amount; i++)
        {
            services.AddSingleton<IHostedService, TaskRunner>();
        }

        return services;
    }
}