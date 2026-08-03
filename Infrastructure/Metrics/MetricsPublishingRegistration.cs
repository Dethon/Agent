using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Metrics;

public static class MetricsPublishingRegistration
{
    // Being a metrics-publishing host is one call. Nothing here is separable: a host that resolved
    // the sink as its caller-facing publisher would turn every publish into a live Redis round
    // trip, and a host that publishes without a heartbeat is missing from the health roster.
    public static IServiceCollection AddMetricsPublishing(this IServiceCollection services, string serviceName) =>
        services
            .AddSingleton<IMetricSink>(sp => new RedisMetricSink(sp.GetRequiredService<IConnectionMultiplexer>()))
            .AddSingleton<IMetricsPublisher>(sp => new BufferedMetricsPublisher(
                sp.GetRequiredService<IMetricSink>(),
                sp.GetService<ILogger<BufferedMetricsPublisher>>()))
            .AddHostedService(sp =>
                new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), serviceName));
}