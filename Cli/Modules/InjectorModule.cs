using Cli.Settings;
using Domain.Contracts;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters.OpenRouter;
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
            httpClient.Timeout = TimeSpan.FromSeconds(60); // Timeout for all attempts combined.
            return new OpenRouterAdapter(httpClient, settings.OpenRouter.Model);
        }).AddRetryWithExponentialWaitPolicy(
            attempts: 3,
            waitTime: TimeSpan.FromSeconds(2),
            attemptTimeout: TimeSpan.FromSeconds(5));

        return services;
    }
}