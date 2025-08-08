using System.Net;
using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Domain.Tools;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters;
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
        return services.AddSingleton<ILargeLanguageModel, OpenAiAdapter>(_ =>
            new OpenAiAdapter(
                settings.OpenRouter.ApiUrl,
                settings.OpenRouter.ApiKey,
                settings.OpenRouter.Models[1]));
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