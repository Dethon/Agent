using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using StackExchange.Redis;

namespace Infrastructure.Metrics;

public sealed class RedisMetricsPublisher(IConnectionMultiplexer redis) : IMetricsPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly RedisChannel Channel = RedisChannel.Literal("metrics:events");
    private readonly ISubscriber _subscriber = redis.GetSubscriber();

    public async Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(metricEvent, JsonOptions);
        await _subscriber.PublishAsync(Channel, json);
    }
}
