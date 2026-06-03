using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ConfidenceGateTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static VoiceConversationManager MakeManager()
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
        return new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
    }

    [Fact]
    public async Task Dispatch_EmptyText_DropsAndPublishesMetric()
    {
        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, new ApprovalCaptureBroker(), MakeManager(), confidenceThreshold: 0.4,
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await dispatcher.DispatchAsync(
            MakeSession(),
            new TranscriptionResult { Text = "", Confidence = 0.9, Language = "en" },
            agentId: "jonas",
            CancellationToken.None);

        ok.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_LowConfidence_DropsAndPublishesMetric()
    {
        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, new ApprovalCaptureBroker(), MakeManager(), confidenceThreshold: 0.4,
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await dispatcher.DispatchAsync(
            MakeSession(),
            new TranscriptionResult { Text = "what?", Confidence = 0.2, Language = "en" },
            agentId: "jonas",
            CancellationToken.None);

        ok.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_GoodTranscript_EmitsAndPublishes()
    {
        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, new ApprovalCaptureBroker(), MakeManager(), confidenceThreshold: 0.4,
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await dispatcher.DispatchAsync(
            MakeSession(),
            new TranscriptionResult { Text = "qué hora es", Confidence = 0.9, Language = "es" },
            agentId: "jonas",
            CancellationToken.None);

        ok.ShouldBeTrue();
        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].Content.ShouldBe("qué hora es");
        emitter.Captured[0].Sender.ShouldBe("household");
        emitter.Captured[0].ConversationId.ShouldNotBeNullOrWhiteSpace();
        emitter.Captured[0].AgentId.ShouldBe("jonas");
    }
}