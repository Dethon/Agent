using System.Net;
using Cli.App;
using Cli.Settings;
using Domain.ChatMonitor;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Attachments;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters.OpenRouter;
using Infrastructure.Wrappers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace Cli.Modules;

public static class InjectorModule
{
    public static IServiceCollection AddAttachments(this IServiceCollection services)
    {
        return services
            .AddScoped<SearchHistory>()
            .AddScoped<DownloadMonitor>();
    }

    public static IServiceCollection AddTools(this IServiceCollection services, AgentConfiguration settings)
    {
        return services
            .AddTransient<FileSearchTool>()
            .AddTransient<FileDownloadTool>(sp => new FileDownloadTool(
                sp.GetRequiredService<IDownloadClient>(),
                sp.GetRequiredService<SearchHistory>(),
                sp.GetRequiredService<DownloadMonitor>(),
                settings.DownloadLocation))
            .AddTransient<WaitForDownloadTool>()
            .AddTransient<LibraryDescriptionTool>(sp => new LibraryDescriptionTool(
                sp.GetRequiredService<IFileSystemClient>(),
                settings.BaseLibraryPath))
            .AddTransient<MoveTool>(sp => new MoveTool(
                sp.GetRequiredService<IFileSystemClient>(),
                settings.BaseLibraryPath))
            .AddTransient<CleanupTool>(sp => new CleanupTool(
                sp.GetRequiredService<IDownloadClient>(),
                sp.GetRequiredService<IFileSystemClient>(),
                settings.DownloadLocation));
    }

    public static IServiceCollection AddJacketClient(this IServiceCollection services, AgentConfiguration settings)
    {
        services.AddHttpClient<ISearchClient, JackettSearchClient>((httpClient, _) =>
            {
                httpClient.BaseAddress = new Uri(settings.Jackett.ApiUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(60); // Timeout for all attempts combined.
                return new JackettSearchClient(httpClient, settings.Jackett.ApiKey);
            })
            .AddRetryWithExponentialWaitPolicy(
                attempts: 3,
                waitTime: TimeSpan.FromSeconds(1),
                attemptTimeout: TimeSpan.FromSeconds(20));

        return services;
    }

    public static IServiceCollection AddQBittorrentClient(this IServiceCollection services, AgentConfiguration settings)
    {
        var cookieContainer = new CookieContainer();
        services.AddHttpClient<IDownloadClient, QBittorrentDownloadClient>((httpClient, _) =>
            {
                httpClient.BaseAddress = new Uri(settings.QBittorrent.ApiUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(60); // Timeout for all attempts combined.
                return new QBittorrentDownloadClient(
                    httpClient,
                    cookieContainer,
                    settings.QBittorrent.UserName,
                    settings.QBittorrent.Password
                );
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true
            })
            .AddRetryWithExponentialWaitPolicy(
                attempts: 3,
                waitTime: TimeSpan.FromSeconds(2),
                attemptTimeout: TimeSpan.FromSeconds(10));

        return services;
    }

    public static IServiceCollection AddFileSystemClient(
        this IServiceCollection services, AgentConfiguration settings, bool sshMode)
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

    public static IServiceCollection AddOpenRouterAdapter(this IServiceCollection services, AgentConfiguration settings)
    {
        services.AddHttpClient<ILargeLanguageModel, OpenRouterAdapter>((httpClient, _) =>
            {
                httpClient.BaseAddress = new Uri(settings.OpenRouter.ApiUrl);
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {settings.OpenRouter.ApiKey}");
                httpClient.Timeout = TimeSpan.FromMinutes(5); // Timeout for all attempts combined.
                return new OpenRouterAdapter(httpClient, settings.OpenRouter.Model);
            })
            .AddRetryWithExponentialWaitPolicy(
                attempts: 3,
                waitTime: TimeSpan.FromSeconds(2),
                attemptTimeout: TimeSpan.FromMinutes(1));

        return services;
    }

    public static IServiceCollection AddChatMonitoring(this IServiceCollection services, AgentConfiguration settings)
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