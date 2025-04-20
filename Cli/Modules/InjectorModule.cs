using System.Net;
using Cli.Settings;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters.OpenRouter;
using Infrastructure.ToolAdapters.FileDownloadTools;
using Infrastructure.ToolAdapters.FileMoveTools;
using Infrastructure.ToolAdapters.FileSearchTools;
using Infrastructure.ToolAdapters.LibraryDescriptionTools;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;

namespace Cli.Modules;

public static class InjectorModule
{
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

    public static IServiceCollection AddJacketTool(this IServiceCollection services, AgentConfiguration settings)
    {
        services.AddHttpClient<FileSearchTool, JackettSearchAdapter>((httpClient, _) =>
            {
                httpClient.BaseAddress = new Uri(settings.Jackett.ApiUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(30); // Timeout for all attempts combined.
                return new JackettSearchAdapter(httpClient, settings.Jackett.ApiKey);
            })
            .AddRetryWithExponentialWaitPolicy(
                attempts: 3,
                waitTime: TimeSpan.FromSeconds(2),
                attemptTimeout: TimeSpan.FromSeconds(20));

        return services;
    }

    public static IServiceCollection AddQBittorrentTool(this IServiceCollection services, AgentConfiguration settings)
    {
        var cookieContainer = new CookieContainer();
        services.AddHttpClient<FileDownloadTool, QBittorrentDownloadAdapter>((httpClient, _) =>
            {
                httpClient.BaseAddress = new Uri(settings.QBittorrent.ApiUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(60); // Timeout for all attempts combined.
                return new QBittorrentDownloadAdapter(
                    httpClient,
                    cookieContainer,
                    settings.QBittorrent.UserName,
                    settings.QBittorrent.Password,
                    settings.DownloadLocation
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

    public static IServiceCollection AddFileManagingTools(
        this IServiceCollection services, AgentConfiguration settings, bool sshMode)
    {
        if (!sshMode)
        {
            return services
                .AddTransient<LibraryDescriptionTool, LocalLibraryDescriptionAdapter>(_ =>
                    new LocalLibraryDescriptionAdapter(settings.BaseLibraryPath))
                .AddTransient<FileMoveTool, LocalFileMoveAdapter>(_ =>
                    new LocalFileMoveAdapter(settings.BaseLibraryPath));
        }

        var sshKey = new PrivateKeyFile(settings.Ssh.KeyPath, settings.Ssh.KeyPass);
        var sshClient = new SshClient(settings.Ssh.Host, settings.Ssh.UserName, sshKey);
        return services
            .AddSingleton(sshClient)
            .AddTransient<FileMoveTool, SshFileMoveAdapter>(_ => new SshFileMoveAdapter(sshClient))
            .AddTransient<LibraryDescriptionTool, SshLibraryDescriptionAdapter>(_ =>
                new SshLibraryDescriptionAdapter(sshClient, settings.BaseLibraryPath));
    }
}