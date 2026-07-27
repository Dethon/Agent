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
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class WakeArbiterTests
{
    private sealed class ListPublisher : IMetricsPublisher
    {
        public readonly List<MetricEvent> Events = [];
        public Task PublishAsync(MetricEvent evt, CancellationToken ct)
        {
            lock (Events)
            { Events.Add(evt); }
            return Task.CompletedTask;
        }
    }

    private sealed class SatelliteHarness
    {
        public readonly SatelliteSession Session;
        public int Paused;
        public int LegacyEnded;
        public UtteranceCapture? Capture;

        public SatelliteHarness(string id, string room, double offsetDb = 0)
        {
            Session = new SatelliteSession(id, new SatelliteConfig
            {
                Identity = "household", Room = room, RmsOffsetDb = offsetDb
            });
        }

        public WakeArbiterHandle Handle => new(
            Session,
            _ => { Interlocked.Increment(ref Paused); return Task.CompletedTask; },
            _ => { Interlocked.Increment(ref LegacyEnded); return Task.CompletedTask; });

        public void OpenCapture(FakeTimeProvider time, ArbitrationSettings settings)
        {
            var gate = new SilenceGate(
                new AdaptiveLevelTracker(500, 9, 4, 15, TimeSpan.FromSeconds(3)),
                TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Capture = Session.OpenCapture(gate, new ChunkHistory(time, settings.HistorySpan));
        }
    }

    private static (WakeArbiter Arbiter, FakeTimeProvider Time, ListPublisher Metrics,
        VoiceConversationManager Conversations) Create(ArbitrationSettings? settings = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var metrics = new ListPublisher();
        var conversations = TestConversationManager(time);
        var arbiter = new WakeArbiter(
            settings ?? new ArbitrationSettings(), conversations, metrics, time,
            NullLogger<WakeArbiter>.Instance);
        return (arbiter, time, metrics, conversations);
    }

    // The exact IConversationFactory fake + ReplyTextAccumulator construction from
    // VoiceConversationManagerTests.Build, so handoff runs against the real manager.
    private static VoiceConversationManager TestConversationManager(FakeTimeProvider clock)
    {
        var factory = new Mock<IConversationFactory>();
        var counter = 0;
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                counter++;
                var topicId = $"topic-{counter}";
                var identity = ConversationIdGenerator.CreateFor(topicId);
                var topic = new TopicMetadata(topicId, identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", clock.GetUtcNow(), null);
                return new ConversationCreation(identity, topic);
            });

        return new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), clock, TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance);
    }

    private static async Task SettleAsync(FakeTimeProvider time, int windowMs)
    {
        // let DecideAfterWindowAsync reach its Task.Delay, then fire it, then let it run
        await Task.Delay(50);
        time.Advance(TimeSpan.FromMilliseconds(windowMs + 1));
        await Task.Delay(50);
    }

    [Fact]
    public async Task Claim_TwoCoincidentWakes_LouderWinsQuieterIsPausedWithoutDispatch()
    {
        var (arbiter, time, metrics, _) = Create();
        var near = new SatelliteHarness("near", "Office A");
        var far = new SatelliteHarness("far", "Office B");
        arbiter.Register("near", near.Handle);
        arbiter.Register("far", far.Handle);
        near.Session.MarkSupportsPause();
        far.Session.MarkSupportsPause();
        near.OpenCapture(time, new ArbitrationSettings());
        far.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("far", 200, 0.8, "wake");
        arbiter.Claim("near", 900, 0.9, "wake");
        await SettleAsync(time, 500);

        far.Paused.ShouldBe(1);
        near.Paused.ShouldBe(0);
        far.Capture!.Completed.IsCompleted.ShouldBeTrue();
        far.Capture.Completed.Result.ShouldBe(CaptureOutcome.Abandoned);
        near.Capture!.Completed.IsCompleted.ShouldBeFalse();
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeSuppressed).SatelliteId.ShouldBe("far");
    }

    [Fact]
    public async Task Claim_RmsOffsetCalibration_FlipsTheWinner()
    {
        var (arbiter, time, _, _) = Create();
        var hot = new SatelliteHarness("hot-mic", "A");             // louder raw, no offset
        var calibrated = new SatelliteHarness("quiet-mic", "B", offsetDb: 12); // +12 dB ~= x3.98
        arbiter.Register("hot-mic", hot.Handle);
        arbiter.Register("quiet-mic", calibrated.Handle);
        hot.Session.MarkSupportsPause();
        calibrated.Session.MarkSupportsPause();
        hot.OpenCapture(time, new ArbitrationSettings());
        calibrated.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("hot-mic", 300, null, "wake");
        arbiter.Claim("quiet-mic", 100, null, "wake"); // 100 * 3.98 = 398 > 300
        await SettleAsync(time, 500);

        hot.Paused.ShouldBe(1);
        calibrated.Paused.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_SingleRegisteredSatellite_IsANoOp()
    {
        var (arbiter, time, metrics, _) = Create();
        var only = new SatelliteHarness("only", "A");
        arbiter.Register("only", only.Handle);
        only.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("only", 500, null, "wake");
        await SettleAsync(time, 500);

        only.Paused.ShouldBe(0);
        only.Capture!.Completed.IsCompleted.ShouldBeFalse();
        metrics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_LegacyLoserWithoutRms_GetsTranscriptFallback()
    {
        var (arbiter, time, _, _) = Create();
        var legacy = new SatelliteHarness("legacy", "A"); // never MarkSupportsPause
        var modern = new SatelliteHarness("modern", "B");
        arbiter.Register("legacy", legacy.Handle);
        arbiter.Register("modern", modern.Handle);
        modern.Session.MarkSupportsPause();
        legacy.OpenCapture(time, new ArbitrationSettings());
        modern.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("legacy", null, null, "wake");
        arbiter.Claim("modern", 400, null, "wake");
        await SettleAsync(time, 500);

        legacy.LegacyEnded.ShouldBe(1);
        legacy.Paused.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_FreshWakeVsQuietOpenHolder_BothProceed()
    {
        // Holder's open capture heard nothing during the wake span -> independent utterances.
        var (arbiter, time, metrics, _) = Create();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();
        holder.OpenCapture(time, new ArbitrationSettings());
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromSeconds(2));
        waker.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("waker", 600, null, "wake");
        await SettleAsync(time, 500);

        holder.Paused.ShouldBe(0);
        waker.Paused.ShouldBe(0);
        metrics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_AlignedLouderHolder_SuppressesTheFreshWakeAsLeak()
    {
        var (arbiter, time, metrics, _) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();

        holder.OpenCapture(time, settings);
        // quiet history, then loud speech exactly at the wake word instant
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(4000));   // the wake word as heard by the holder's mic
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 300, null, "wake"); // far away: much quieter than the holder heard
        await SettleAsync(time, settings.WindowMs);

        waker.Paused.ShouldBe(1);
        holder.Paused.ShouldBe(0);
        holder.Capture.Completed.IsCompleted.ShouldBeFalse("the holder keeps its capture");
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeSuppressed).Outcome.ShouldBe("leak");
    }

    [Fact]
    public async Task Claim_AlignedMuchLouderFreshWake_HandsOffTheConversation()
    {
        var (arbiter, time, metrics, conversations) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();
        var conversationId = await conversations.GetOrCreateAsync(
            holder.Session, "agent", "hola", CancellationToken.None);

        holder.OpenCapture(time, settings);
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(600));    // faint leak of the wake word said far from A
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 5000, null, "wake"); // user is right next to B: > 6 dB louder
        await SettleAsync(time, settings.WindowMs);

        holder.Paused.ShouldBe(1);
        holder.Capture.Completed.Result.ShouldBe(CaptureOutcome.Abandoned);
        waker.Paused.ShouldBe(0);
        conversations.GetActiveConversationId("waker").ShouldBe(conversationId);
        conversations.GetActiveConversationId("holder").ShouldBeNull();
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeHandoff).SatelliteId.ShouldBe("waker");
    }

    private static AudioChunk SilentChunk() => PcmChunk(0);
    private static AudioChunk LoudChunk(short amplitude) => PcmChunk(amplitude);

    private static AudioChunk PcmChunk(short amplitude, int samples = 1280)
    {
        var bytes = new byte[samples * 2];
        foreach (var i in Enumerable.Range(0, samples))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), amplitude);
        }
        return new AudioChunk
        {
            Data = bytes,
            Format = new AudioFormat { SampleRateHz = 16000, SampleWidthBytes = 2, Channels = 1 },
            Timestamp = TimeSpan.Zero
        };
    }
}