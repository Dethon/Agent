using Domain.Contracts;
using Infrastructure.Clients.HomeAssistant;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

public static class HomeAssistantClientExtensions
{
    public static IServiceCollection AddHomeAssistantClient(
        this IServiceCollection services, string baseUrl, string token)
    {
        services.AddHttpClient<IHomeAssistantClient, HomeAssistantClient>((http, _) =>
        {
            http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            http.Timeout = TimeSpan.FromSeconds(30);
            return new HomeAssistantClient(http, token);
        })
        .AddRetryWithExponentialWaitPolicy(
            attempts: 2,
            waitTime: TimeSpan.FromSeconds(1),
            attemptTimeout: TimeSpan.FromSeconds(15));

        return services;
    }
}