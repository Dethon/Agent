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
            attemptTimeout: TimeSpan.FromSeconds(15),
            retryable: request => !IsServiceCall(request));

        return services;
    }

    // POST /api/services/<domain>/<service> runs a service handler; HA reports ANY exception it
    // raises as a 500, so a 5xx there is a deterministic failure (an unresolvable media name, a
    // device that refused) rather than a transient one. Retrying only adds latency to the reply and
    // risks re-applying a partial effect. GET reads and POST /api/template stay retryable.
    private static bool IsServiceCall(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post
        && request.RequestUri?.AbsolutePath.Contains("/api/services/", StringComparison.Ordinal) == true;
}