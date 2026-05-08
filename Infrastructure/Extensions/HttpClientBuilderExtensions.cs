using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace Infrastructure.Extensions;

public static class HttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddRetryWithExponentialWaitPolicy(
        this IHttpClientBuilder builder,
        int attempts,
        TimeSpan waitTime,
        TimeSpan attemptTimeout)
    {
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(attempts, attempt => TimeSpan.FromSeconds(Math.Pow(waitTime.TotalSeconds, attempt)));

        var singleTryTimeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(attemptTimeout);

        return builder
            .AddPolicyHandler(retryPolicy)
            .AddPolicyHandler(singleTryTimeoutPolicy);
    }

    public static IHttpClientBuilder AddRetryOnRateLimitPolicy(
        this IHttpClientBuilder builder,
        int attempts,
        TimeSpan waitTime)
    {
        var rateLimitPolicy = Policy<HttpResponseMessage>
            .HandleResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(attempts, _ => waitTime);

        return builder.AddPolicyHandler(rateLimitPolicy);
    }
}