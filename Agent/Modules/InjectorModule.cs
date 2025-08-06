using System.Net;
using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Domain.Tools;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters.OpenRouter;
using Infrastructure.Wrappers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace Agent.Modules;

public static class InjectorModule
{
    public static IServiceCollection AddFileSystemClient(
        this IServiceCollection services, AgentSettings settings, bool sshMode)
    {
        if (!sshMode)
        {
            return services.AddTransient<IFileSystemClient, LocalFileSystemClient>();
        }

        var sshClient = new SshClientWrapper(settings.Ssh.Host, settings.Ssh.UserName, settings.Ssh.KeyPath,
            settings.Ssh.KeyPass);
        return services
            .AddSingleton(sshClient)
            .AddTransient<IFileSystemClient, SshFileSystemClient>(_ => new SshFileSystemClient(sshClient));
    }

    public static IServiceCollection AddOpenRouterAdapter(this IServiceCollection services, AgentSettings settings)
    {
        services.AddHttpClient<ILargeLanguageModel, OpenRouterAdapter>((httpClient, _) =>
            {
                httpClient.BaseAddress = new Uri(settings.OpenRouter.ApiUrl);
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {settings.OpenRouter.ApiKey}");
                httpClient.Timeout = TimeSpan.FromMinutes(5); // Timeout for all attempts combined.
                return new OpenRouterAdapter(httpClient, settings.OpenRouter.Models);
            })
            .AddRetryWithExponentialWaitPolicy(
                attempts: 3,
                waitTime: TimeSpan.FromSeconds(2),
                attemptTimeout: TimeSpan.FromMinutes(1));

        return services;
    }

    public static IServiceCollection AddChatMonitoring(this IServiceCollection services, AgentSettings settings)
    {
        return services
            .AddSingleton<TaskQueue>()
            .AddSingleton<ChatMonitor>()
            .AddSingleton<IChatClient, TelegramBotChatClient>(_ =>
                new TelegramBotChatClient(
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