using Cli.Settings;
using Domain.Contracts;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters;
using Microsoft.Extensions.DependencyInjection;

namespace Cli.Modules;

public static class InjectorModule
{
    public static IServiceCollection AddOpenRouterAdapter(this IServiceCollection services, AgentConfiguration settings)
    {
        services.AddHttpClient<ILargeLanguageModel, OpenRouterAdapter>((p, c) =>
        {
            c.BaseAddress = new Uri(settings.OpenRouterApiUrl);
            c.DefaultRequestHeaders.Add("Authorization", $"Bearer {settings.OpenRouterApiKey}");
            c.Timeout = TimeSpan.FromSeconds(60); // Timeout for all attempts combined.
        }).AddRetryWithExponentialWaitPolicy(
            attempts: 3,
            waitTime: TimeSpan.FromSeconds(2),
            attemptTimeout: TimeSpan.FromSeconds(5));
        
        return services;
    }
}