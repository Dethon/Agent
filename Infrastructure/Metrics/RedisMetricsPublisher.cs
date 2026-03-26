using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using StackExchange.Redis;

namespace Infrastructure.Metrics;

public sealed class RedisMetricsPublisher(IConnectionMultiplexer redis) : IMetricsPublisher
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly RedisChannel _channel = RedisChannel.Literal("metrics:events");
    private readonly ISubscriber _subscriber = redis.GetSubscriber();

    public async Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(metricEvent, _jsonOptions);
        await _subscriber.PublishAsync(_channel, json);
    }
}