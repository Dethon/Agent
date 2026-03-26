using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Moq;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Metrics;

public class RedisMetricsPublisherTests
{
    private readonly Mock<ISubscriber> _subscriber = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly RedisMetricsPublisher _sut;

    public RedisMetricsPublisherTests()
    {
        _redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_subscriber.Object);
        _sut = new RedisMetricsPublisher(_redis.Object);
    }

    public static TheoryData<MetricEvent, string> EventCases => new()
    {
        {
            new HeartbeatEvent { Service = "agent" },
            "\"service\":\"agent\""
        },
        {
            new TokenUsageEvent
            {
                Sender = "user1",
                Model = "gpt-4",
                InputTokens = 100,
                OutputTokens = 50,
                Cost = 0.01m
            },
            "\"type\":\"token_usage\""
        }
    };

    [Theory]
    [MemberData(nameof(EventCases))]
    public async Task PublishAsync_publishes_serialized_event_to_metrics_channel(MetricEvent evt, string expectedFragment)
    {
        await _sut.PublishAsync(evt);

        _subscriber.Verify(s => s.PublishAsync(
            RedisChannel.Literal("metrics:events"),
            It.Is<RedisValue>(v => v.ToString().Contains(expectedFragment)),
            It.IsAny<CommandFlags>()), Times.Once);
    }
}