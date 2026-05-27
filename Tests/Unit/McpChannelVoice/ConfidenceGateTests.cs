using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ConfidenceGateTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private sealed class CapturingEmitter : ChannelNotificationEmitter
    {
        public List<ChannelMessageNotification> Captured { get; } = new();
        public CapturingEmitter() : base(NullLogger<ChannelNotificationEmitter>.Instance) { }
        public override Task EmitMessageNotificationAsync(ChannelMessageNotification p, CancellationToken ct = default)
        {
            Captured.Add(p);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Dispatch_EmptyText_DropsAndPublishesMetric()
    {
        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, confidenceThreshold: 0.4,
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
            emitter, publisher.Object, confidenceThreshold: 0.4,
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
            emitter, publisher.Object, confidenceThreshold: 0.4,
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
        emitter.Captured[0].ConversationId.ShouldBe("kitchen-01");
        emitter.Captured[0].AgentId.ShouldBe("jonas");
    }
}