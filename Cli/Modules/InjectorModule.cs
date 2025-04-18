using Cli.Settings;
using Domain.Contracts;
using Domain.Tools;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters.OpenRouter;
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
        }).AddRetryWithExponentialWaitPolicy(
            attempts: 3,
            waitTime: TimeSpan.FromSeconds(2),
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
        }).AddRetryWithExponentialWaitPolicy(
            3,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(2));

        return services;
    }
}