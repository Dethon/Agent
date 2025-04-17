using Microsoft.Extensions.DependencyInjection;
using Polly.Extensions.Http;
using Polly.Timeout;
using Polly;

namespace Infrastructure.Extensions;
public static class HttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddRetryWithExponentialWaitPolicy(this IHttpClientBuilder builder, int attempts, TimeSpan waitTime, TimeSpan attemptTimeout)
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
}