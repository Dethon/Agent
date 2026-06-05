using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
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

    [Fact]
    public async Task PublishAsync_serializes_heartbeat_event_to_metrics_channel()
    {
        await _sut.PublishAsync(new HeartbeatEvent { Service = "agent" });

        VerifyPublished("\"service\":\"agent\"");
    }

    [Fact]
    public async Task PublishAsync_serializes_token_usage_event_to_metrics_channel()
    {
        await _sut.PublishAsync(new TokenUsageEvent
        {
            Sender = "user1",
            Model = "gpt-4",
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.01m
        });

        VerifyPublished("\"type\":\"token_usage\"");
    }

    [Fact]
    public async Task PublishAsync_serializes_latency_event_to_metrics_channel()
    {
        await _sut.PublishAsync(new LatencyEvent
        {
            Stage = LatencyStage.LlmTotal,
            DurationMs = 1234,
            Model = "anthropic/claude",
            ConversationId = "conv1"
        });

        VerifyPublished("\"type\":\"latency\"");
    }

    private void VerifyPublished(string expectedFragment) =>
        _subscriber.Verify(s => s.PublishAsync(
            RedisChannel.Literal("metrics:events"),
            It.Is<RedisValue>(v => v.ToString().Contains(expectedFragment)),
            It.IsAny<CommandFlags>()), Times.Once);
}