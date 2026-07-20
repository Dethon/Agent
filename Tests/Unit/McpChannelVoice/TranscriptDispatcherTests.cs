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
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new TranscriptDispatcher(
            emitter, Mock.Of<IMetricsPublisher>(), manager,
            confidenceThreshold: 0.5, time, NullLogger<TranscriptDispatcher>.Instance);
        return (sut, manager, emitter);
    }

    [Fact]
    public async Task DispatchAsync_GoodTranscript_OpensConversationViaManager()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", null, default);

        ok.ShouldBeTrue();
        var convo = manager.GetActiveConversationId("kitchen-01");
        convo.ShouldNotBeNull();
        manager.ResolveSatelliteId(convo).ShouldBe("kitchen-01");

        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].ConversationId.ShouldBe(convo);
        emitter.Captured[0].Content.ShouldBe("what time is it");
        emitter.Captured[0].Sender.ShouldBe("household");
        emitter.Captured[0].AgentId.ShouldBe("agent-1");
    }

    [Fact]
    public async Task DispatchAsync_GoodTranscript_EmitsRoomAsLocation()
    {
        var (sut, _, emitter) = Build();

        await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", null, default);

        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].Location.ShouldBe("Kitchen");
    }

    [Fact]
    public async Task DispatchAsync_SatelliteWithLocality_EmitsRoomAndLocalityAsLocation()
    {
        var (sut, _, emitter) = Build();
        var session = new SatelliteSession(
            "kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen", Locality = "Madrid, Spain" });

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "what's the weather", Confidence = 0.9 }, "agent-1", null, default);

        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].Location.ShouldBe("Kitchen (Madrid, Spain)");
    }

    [Fact]
    public async Task DispatchAsync_GoodTranscript_EmitsSatelliteId()
    {
        var (sut, _, emitter) = Build();

        await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", null, default);

        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].SatelliteId.ShouldBe("kitchen-01");
    }

    [Fact]
    public async Task DispatchAsync_LowConfidence_DoesNotOpenConversation()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "mumble", Confidence = 0.1 }, "agent-1", null, default);

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
            emitter, publisher.Object, manager,
            confidenceThreshold: 0.5, new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "   ", Confidence = 0.9 }, "agent-1", null, default);

        ok.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        factory.Verify(
            f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()),
            Times.Never);
        published.OfType<VoiceEvent>()
            .ShouldContain(e => e.Metric == VoiceMetric.UtteranceTranscribed && e.Outcome == "dropped");
    }

    [Fact]
    public async Task DispatchAsync_AfterDismissal_EmitsDismissedAlertOnce()
    {
        var (sut, _, emitter) = Build();
        var session = Session();
        session.NoteDismissedAlert("alarm \"trash\"", DateTimeOffset.UtcNow);

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "five more minutes", Confidence = 0.9 }, "agent-1", null, default);
        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "thanks", Confidence = 0.9 }, "agent-1", null, default);

        emitter.Captured.Count.ShouldBe(2);
        emitter.Captured[0].DismissedAlert.ShouldBe("alarm \"trash\"");
        emitter.Captured[1].DismissedAlert.ShouldBeNull(); // consumed by the first dispatch
    }

    [Fact]
    public async Task DispatchAsync_Dispatched_PublishesCaptureAndWhisperStats()
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
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(Task.CompletedTask);
        var sut = new TranscriptDispatcher(
            new CapturingEmitter(), publisher.Object, manager,
            confidenceThreshold: 0.5, new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(),
            new TranscriptionResult
            {
                Text = "hola",
                Confidence = 0.8,
                AvgLogProb = -0.22,
                NoSpeechProb = 0.05,
                CompressionRatio = 1.3
            },
            "agent-1",
            new CaptureStats(PeakRms: 4200, FloorRms: 320, SpeechMs: 1800, EndReason: "trailing_silence", TrailingRms: 610),
            default);

        ok.ShouldBeTrue();
        var evt = published.OfType<VoiceEvent>().Single(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        evt.Outcome.ShouldBe("dispatched");
        evt.Confidence.ShouldBe(0.8);
        evt.AvgLogProb.ShouldBe(-0.22);
        evt.NoSpeechProb.ShouldBe(0.05);
        evt.CompressionRatio.ShouldBe(1.3);
        evt.PeakRms.ShouldBe(4200);
        evt.SpeechMs.ShouldBe(1800);
        evt.FloorRms.ShouldBe(320);
        evt.TrailingRms.ShouldBe(610);
        evt.EndReason.ShouldBe("trailing_silence");
    }

    [Fact]
    public async Task DispatchAsync_Dropped_PublishesCaptureAndWhisperStats()
    {
        var manager = new VoiceConversationManager(
            new Mock<IConversationFactory>().Object, new ReplyTextAccumulator(),
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance);
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(Task.CompletedTask);
        var sut = new TranscriptDispatcher(
            new CapturingEmitter(), publisher.Object, manager,
            confidenceThreshold: 0.5, new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(),
            new TranscriptionResult
            {
                Text = "grbll xzzt",
                Confidence = 0.12,
                AvgLogProb = -2.1,
                NoSpeechProb = 0.55,
                CompressionRatio = 2.8
            },
            "agent-1",
            new CaptureStats(PeakRms: 900, FloorRms: 610, SpeechMs: 450, EndReason: "max_utterance"),
            default);

        ok.ShouldBeFalse();
        var evt = published.OfType<VoiceEvent>().Single(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        evt.Outcome.ShouldBe("dropped");
        evt.Confidence.ShouldBe(0.12);
        evt.AvgLogProb.ShouldBe(-2.1);
        evt.NoSpeechProb.ShouldBe(0.55);
        evt.CompressionRatio.ShouldBe(2.8);
        evt.PeakRms.ShouldBe(900);
        evt.SpeechMs.ShouldBe(450);
        evt.FloorRms.ShouldBe(610);
        evt.EndReason.ShouldBe("max_utterance");
    }
}