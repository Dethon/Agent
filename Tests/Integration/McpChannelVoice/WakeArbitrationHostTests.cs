using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpChannelVoice;

// The only proof that multi-satellite wake arbitration works over a real socket with two real
// satellite connections: every other test in the feature drives the arbiter with hand-built
// objects. Real TimeProvider, real TCP, short windows — the whole point is the scheduler and the
// wire, so a FakeTimeProvider here would test nothing these tests exist to test.
public class WakeArbitrationHostTests
{
    private const int ArbitrationWindowMs = 300;
    private const int ReplyTimeoutMs = 500;
    // 1600 samples of 16 kHz mono S16LE = 100 ms per chunk, matching the satellite's frame size
    // closely enough that the gate's audio-domain arithmetic lands on round numbers.
    private const int ChunkBytes = 3200;

    private static readonly TimeSpan _wireTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CoincidentWakes_OneDispatch_LoserGetsPauseSatellite()
    {
        await using var satA = new FakeSatelliteServer();
        await using var satB = new FakeSatelliteServer();
        var hub = BuildHub(satA, satB);

        await hub.Host.StartAsync(CancellationToken.None);
        try
        {
            await satA.AcceptAsync();
            await satB.AcceptAsync();
            await satA.WaitForEventAsync("run-satellite", _wireTimeout);
            await satB.WaitForEventAsync("run-satellite", _wireTimeout);

            // Both hear the same utterance inside the arbitration window; A's mic is closer.
            await satA.SendAsync(RunPipeline(wakeRms: 900));
            await satB.SendAsync(RunPipeline(wakeRms: 200));
            await StreamSpeechAsync(satA);
            await StreamSpeechAsync(satB);

            // The decision lands while both captures are still open — which is what happens in the
            // field, where the arbitration window closes long before anyone stops talking.
            await satB.WaitForEventAsync("pause-satellite", _wireTimeout);

            // Both satellites now finish the utterance. B keeps streaming deliberately: a suppressed
            // satellite must be unable to dispatch even if its audio keeps arriving, otherwise this
            // test's "exactly one message" would only be measuring that B was never asked to finish.
            await StreamTrailingSilenceAsync(satA);
            await StreamTrailingSilenceAsync(satB);

            // A's turn ends on the reply-handshake timeout (nothing replies in this harness), which
            // is the last thing to happen on either connection — so B has had every chance to
            // dispatch by the time the counts below are read.
            await satA.WaitForEventAsync("transcript", _wireTimeout);

            satB.CountOf("transcript").ShouldBe(0); // suppression is silent: no done cue on the loser
            satA.CountOf("pause-satellite").ShouldBe(0);

            hub.Emitter.Messages.Count.ShouldBe(1);
            hub.Emitter.Messages[0].SatelliteId.ShouldBe("a");
            hub.Emitter.Messages[0].Content.ShouldBe("hola");

            // Pins WHY B went quiet. Without this the assertions above would also pass if B had lost
            // to Rule B (leak) or simply stalled; only Rule A's loudness comparison reports this.
            var suppressed = hub.Metrics.VoiceEvents
                .Single(e => e.Metric == VoiceMetric.WakeSuppressed);
            suppressed.SatelliteId.ShouldBe("b");
            suppressed.Outcome.ShouldBe("lost_loudness");
            suppressed.WakeRms.ShouldBe(200);
        }
        finally
        {
            await hub.Host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WakeDuringAnotherSatellitesOpenCapture_MuchLouder_StealsTheTurn()
    {
        await using var satA = new FakeSatelliteServer();
        await using var satB = new FakeSatelliteServer();
        var hub = BuildHub(satA, satB);

        await hub.Host.StartAsync(CancellationToken.None);
        try
        {
            await satA.AcceptAsync();
            await satB.AcceptAsync();
            await satA.WaitForEventAsync("run-satellite", _wireTimeout);
            await satB.WaitForEventAsync("run-satellite", _wireTimeout);

            // A wakes alone and starts talking, then holds the capture open (no trailing silence).
            await satA.SendAsync(RunPipeline(wakeRms: 600));
            await StreamSpeechAsync(satA);

            // A's own arbitration window must close before B claims, or B joins it and the outcome
            // below would be Rule A (loudest of two claims), not the Rule B steal this test exists
            // to pin. Nothing on the wire marks a solo window closing, so the separation has to be
            // wall-clock; 3x the window is the margin.
            await Task.Delay(TimeSpan.FromMilliseconds(ArbitrationWindowMs * 3));

            // A is still mid-sentence when B wakes. Streaming this burst immediately before B's
            // claim is what keeps the test robust: Rule B needs a speech onset inside the
            // reconstructed wake-word span, and an onset anchored to B's claim stays inside it
            // however far the delay above overshoots.
            await StreamSpeechAsync(satA);
            await satB.SendAsync(RunPipeline(wakeRms: 20_000));
            await StreamSpeechAsync(satB);

            await satA.WaitForEventAsync("pause-satellite", _wireTimeout);
            await StreamTrailingSilenceAsync(satB);
            await satB.WaitForEventAsync("transcript", _wireTimeout);

            satB.CountOf("pause-satellite").ShouldBe(0); // the challenger keeps its turn
            satA.CountOf("transcript").ShouldBe(0);      // the robbed holder is silenced, not done-cued

            hub.Emitter.Messages.Count.ShouldBe(1);
            hub.Emitter.Messages[0].SatelliteId.ShouldBe("b");

            // The discriminator: a Rule A loss would report WakeSuppressed/lost_loudness against A.
            // Only the steal path publishes WakeHandoff, and only it names the displaced holder.
            var handoff = hub.Metrics.VoiceEvents.Single(e => e.Metric == VoiceMetric.WakeHandoff);
            handoff.SatelliteId.ShouldBe("b");
            handoff.Outcome.ShouldBe("a");
            hub.Metrics.VoiceEvents.ShouldNotContain(e => e.Metric == VoiceMetric.WakeSuppressed);
        }
        finally
        {
            await hub.Host.StopAsync(CancellationToken.None);
        }
    }

    private sealed record Hub(
        WyomingSatelliteHost Host, RecordingEmitter Emitter, RecordingMetrics Metrics);

    private static Hub BuildHub(FakeSatelliteServer satA, FakeSatelliteServer satB)
    {
        var voiceSettings = new VoiceSettings
        {
            AgentId = "mycroft",
            Satellites = new Dictionary<string, SatelliteConfig>
            {
                ["a"] = new() { Identity = "household", Room = "A", Address = satA.Address },
                ["b"] = new() { Identity = "household", Room = "B", Address = satB.Address }
            },
            WyomingClient = new WyomingClientSettings
            {
                ReconnectDelaySeconds = 1,
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 30_000,
                MinSpeechMs = 80
            },
            FollowUp = new FollowUpSettings { Enabled = true, ReplyTimeoutMs = ReplyTimeoutMs },
            Arbitration = new ArbitrationSettings { WindowMs = ArbitrationWindowMs }
        };

        var emitter = new RecordingEmitter();
        var metrics = new RecordingMetrics();
        var factory = new Mock<IConversationFactory>();
        var minted = 0;
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var topicName = $"topic-{Interlocked.Increment(ref minted)}";
                var identity = ConversationIdGenerator.CreateFor(topicName);
                var topic = new TopicMetadata(topicName, identity.ChatId, identity.ThreadId, "mycroft",
                    "household", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), TimeProvider.System,
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var dispatcher = new TranscriptDispatcher(
            emitter, metrics, manager, -1.0, 0.6, -1.4, 2000, TimeProvider.System,
            NullLogger<TranscriptDispatcher>.Instance);
        var arbiter = new WakeArbiter(
            voiceSettings.Arbitration, manager, metrics, TimeProvider.System,
            NullLogger<WakeArbiter>.Instance);

        var host = new WyomingSatelliteHost(
            voiceSettings.WyomingClient,
            voiceSettings,
            new SatelliteRegistry(voiceSettings.Satellites),
            new SatelliteSessionRegistry(),
            manager,
            new FixedSpeechToText(),
            dispatcher,
            new ActiveAlertRegistry(),
            metrics,
            TimeProvider.System,
            arbiter,
            NullLogger<WyomingSatelliteHost>.Instance);

        return new Hub(host, emitter, metrics);
    }

    // A silent lead-in frame then three loud ones: real captures open on the ambient gap left by
    // wake-detection latency, and the AdaptiveLevelTracker needs that frame to seed its noise floor
    // before anything can classify as speech (it seeds from the first real audio, so an opening
    // burst would become its own floor and never latch).
    private static async Task StreamSpeechAsync(FakeSatelliteServer satellite)
    {
        await satellite.SendAsync(AudioChunkEvent(0));
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await satellite.SendAsync(AudioChunkEvent(3000));
        }
    }

    // 400 ms of silence, twice the configured trailing-silence endpoint.
    private static async Task StreamTrailingSilenceAsync(FakeSatelliteServer satellite)
    {
        foreach (var _ in Enumerable.Range(0, 4))
        {
            await satellite.SendAsync(AudioChunkEvent(0));
        }
    }

    private static WyomingEvent RunPipeline(double wakeRms) =>
        WyomingEvent.Header("run-pipeline", new JsonObject
        {
            ["source"] = "wake",
            ["wake_rms"] = wakeRms,
            ["wake_score"] = 0.9
        });

    private static WyomingEvent AudioChunkEvent(short amplitude)
    {
        var bytes = new byte[ChunkBytes];
        foreach (var i in Enumerable.Range(0, ChunkBytes / 2))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), amplitude);
        }
        return WyomingEvent.WithPayload("audio-chunk",
            new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 }, bytes);
    }

    private sealed class FixedSpeechToText : ISpeechToText
    {
        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            await foreach (var _ in audio.WithCancellation(ct))
            { }
            return new TranscriptionResult { Text = "hola", Language = "es", Confidence = 0.9 };
        }
    }

    // Locked, unlike the single-satellite CapturingEmitter: two connections can reach the emitter
    // concurrently here, and an unsynchronized List would corrupt exactly when the feature is
    // broken — turning the failure this test exists to catch into an unrelated crash.
    private sealed class RecordingEmitter : ChannelNotificationEmitter
    {
        private readonly List<ChannelMessageNotification> _messages = [];
        private readonly Lock _gate = new();

        public RecordingEmitter() : base(new ChannelInbox()) { }

        public IReadOnlyList<ChannelMessageNotification> Messages
        {
            get
            {
                lock (_gate)
                {
                    return _messages.ToArray();
                }
            }
        }

        public override Task EmitMessageNotificationAsync(
            string conversationId, string sender, string content, string? agentId, string? location,
            string? satelliteId, string? dismissedAlert, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _messages.Add(new ChannelMessageNotification
                {
                    ConversationId = conversationId,
                    Sender = sender,
                    Content = content,
                    AgentId = agentId,
                    Location = location,
                    SatelliteId = satelliteId,
                    DismissedAlert = dismissedAlert
                });
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMetrics : IMetricsPublisher
    {
        private readonly List<VoiceEvent> _events = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<VoiceEvent> VoiceEvents
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            if (metricEvent is VoiceEvent voice)
            {
                lock (_gate)
                {
                    _events.Add(voice);
                }
            }
            return Task.CompletedTask;
        }
    }
}