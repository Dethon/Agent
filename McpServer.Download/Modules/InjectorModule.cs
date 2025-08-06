using System.Net;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using McpServer.Download.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace McpServer.Download.Modules;

public static class InjectorModule
{
    public static IServiceCollection AddTools(this IServiceCollection services, McpSettings mcpSettings)
    {
        return services
            .AddTransient<FileSearchTool>()
            .AddTransient<FileDownloadTool>(sp => new FileDownloadTool(
                sp.GetRequiredService<IDownloadClient>(),
                sp.GetRequiredService<IMemoryCache>(),
                mcpSettings.DownloadLocation))
            .AddTransient<GetDownloadStatusTool>(sp => new GetDownloadStatusTool(
                sp.GetRequiredService<IDownloadClient>(),
                sp.GetRequiredService<IMemoryCache>()));
    }

    public static IServiceCollection AddJacketClient(this IServiceCollection services, McpSettings settings)
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

    public static IServiceCollection AddQBittorrentClient(this IServiceCollection services, McpSettings settings)
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
}