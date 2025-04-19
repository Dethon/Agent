using System.Net;
using Cli.Settings;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters.OpenRouter;
using Infrastructure.ToolAdapters.FileDownloadTools;
using Infrastructure.ToolAdapters.FileSearchTools;
using Microsoft.Extensions.DependencyInjection;

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
                3,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMinutes(1));

        return services;
    }

    public static IServiceCollection AddJacketTool(this IServiceCollection services, AgentConfiguration settings)
    {
        services.AddHttpClient<FileSearchTool, JackettSearchAdapter>((httpClient, _) =>
            {
                httpClient.BaseAddress = new Uri(settings.Jackett.ApiUrl);
                httpClient.Timeout = TimeSpan.FromMinutes(10); // Timeout for all attempts combined.
                return new JackettSearchAdapter(httpClient, settings.Jackett.ApiKey);
            })
            .AddRetryWithExponentialWaitPolicy(
                3,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMinutes(2));

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
                    settings.QBittorrent.User,
                    settings.QBittorrent.Password,
                    cookieContainer);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true
            })
            .AddRetryWithExponentialWaitPolicy(
                3,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10));

        return services;
    }
}