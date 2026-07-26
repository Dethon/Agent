using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace Infrastructure.Extensions;

public static class HttpClientBuilderExtensions
{
    // `retryable` opts individual requests out of the retry policy while keeping the per-attempt
    // timeout. Use it for endpoints whose 5xx means "the call failed", not "try again" — replaying
    // those burns a backoff round for nothing and can re-run a side effect that partly applied.
    public static IHttpClientBuilder AddRetryWithExponentialWaitPolicy(
        this IHttpClientBuilder builder,
        int attempts,
        TimeSpan waitTime,
        TimeSpan attemptTimeout,
        Func<HttpRequestMessage, bool>? retryable = null)
    {
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(attempts, attempt => TimeSpan.FromSeconds(Math.Pow(waitTime.TotalSeconds, attempt)));

        var noRetryPolicy = Policy.NoOpAsync<HttpResponseMessage>();
        var singleTryTimeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(attemptTimeout);

        return builder
            .AddPolicyHandler(request => retryable?.Invoke(request) == false ? noRetryPolicy : retryPolicy)
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