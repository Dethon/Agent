using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TranscriptDispatcherTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static (TranscriptDispatcher Sut, VoiceConversationManager Manager, CapturingEmitter Emitter) Build()
    {
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        var emitter = new CapturingEmitter();
        var sut = new TranscriptDispatcher(
            emitter, Mock.Of<IMetricsPublisher>(), new ApprovalCaptureBroker(), manager,
            confidenceThreshold: 0.5, NullLogger<TranscriptDispatcher>.Instance);
        return (sut, manager, emitter);
    }

    [Fact]
    public async Task DispatchAsync_GoodTranscript_OpensConversationViaManager()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", default);

        ok.ShouldBeTrue();
        var convo = manager.GetActiveConversationId("kitchen-01");
        convo.ShouldNotBeNull();
        manager.ResolveSatelliteId(convo).ShouldBe("kitchen-01");

        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].ConversationId.ShouldBe(convo);
    }

    [Fact]
    public async Task DispatchAsync_GoodTranscript_EmitsRoomAsLocation()
    {
        var (sut, _, emitter) = Build();

        await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", default);

        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].Location.ShouldBe("Kitchen");
    }

    [Fact]
    public async Task DispatchAsync_LowConfidence_DoesNotOpenConversation()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "mumble", Confidence = 0.1 }, "agent-1", default);

        ok.ShouldBeFalse();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_EmptyText_DropsAndPublishesDroppedMetric()
    {
        var factory = new Mock<IConversationFactory>();
        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var emitter = new CapturingEmitter();
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(Task.CompletedTask);
        var sut = new TranscriptDispatcher(
            emitter, publisher.Object, new ApprovalCaptureBroker(), manager,
            confidenceThreshold: 0.5, NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "   ", Confidence = 0.9 }, "agent-1", default);

        ok.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        factory.Verify(
            f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()),
            Times.Never);
        published.OfType<VoiceEvent>()
            .ShouldContain(e => e.Metric == VoiceMetric.UtteranceTranscribed && e.Outcome == "dropped");
    }
}