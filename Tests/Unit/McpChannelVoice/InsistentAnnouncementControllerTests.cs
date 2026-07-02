using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class InsistentAnnouncementControllerTests
{
    private sealed class CollectingPublisher : IMetricsPublisher
    {
        private readonly List<VoiceEvent> _events = [];
        public IReadOnlyList<VoiceEvent> Events
        {
            get { lock (_events) { return _events.ToList(); } }
        }
        public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            if (metricEvent is VoiceEvent v)
            {
                lock (_events)
                { _events.Add(v); }
            }
            return Task.CompletedTask;
        }
    }

    private static async IAsyncEnumerable<AudioChunk> OneChunk()
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.CompletedTask;
    }

    private sealed record Harness(
        InsistentAnnouncementController Controller,
        SatelliteSessionRegistry Sessions,
        ActiveAlertRegistry Alerts,
        CollectingPublisher Publisher,
        Mock<ITextToSpeech> Tts,
        FakeTimeProvider Time);

    private static Harness BuildHarness(FakeTimeProvider time, bool online, params string[] satelliteIds)
    {
        var configs = satelliteIds.ToDictionary(
            id => id, id => new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        var registry = new SatelliteRegistry(configs);
        var sessions = new SatelliteSessionRegistry();
        if (online)
        {
            foreach (var id in satelliteIds)
            {
                sessions.Register(new SatelliteSession(id, configs[id]));
            }
        }

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => OneChunk());

        var alerts = new ActiveAlertRegistry();
        var publisher = new CollectingPublisher();
        var controller = new InsistentAnnouncementController(
            registry, sessions, tts.Object, new VoiceSettings(), alerts, publisher, time,
            NullLogger<InsistentAnnouncementController>.Instance);
        return new Harness(controller, sessions, alerts, publisher, tts, time);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            { throw new TimeoutException("condition not met"); }
            await Task.Delay(20);
        }
    }

    // Runs each online session's playback loop so enqueued jobs actually play; the writer counts
    // one invocation per round (the mock TTS yields exactly one chunk).
    private static (Task Pump, Func<int> Count) PumpPlays(SatelliteSession session, FakeTimeProvider time)
    {
        var count = 0;
        var pump = session.RunPlaybackLoopAsync(
            (_, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            CancellationToken.None, time);
        return (pump, () => Volatile.Read(ref count));
    }

    [Fact]
    public async Task Start_UnknownTarget_Throws()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, "kitchen-01");

        await Should.ThrowAsync<AnnounceTargetNotFoundException>(() =>
            h.Controller.StartAsync(
                new AnnounceRequest { Target = new() { SatelliteId = "ghost" }, Text = "alarm", Insistent = new() },
                CancellationToken.None));
    }

    [Fact]
    public async Task Start_NoOnlineSession_PublishesAlarmOffline()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: false, "kitchen-01"); // configured but not connected

        var response = await h.Controller.StartAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "alarm", Insistent = new() },
            CancellationToken.None);

        response.Satellites.ShouldHaveSingleItem();
        response.Satellites[0].Status.ShouldBe("offline");
        h.Publisher.Events.ShouldContain(e => e.Metric == VoiceMetric.AlarmOffline);
    }

    [Fact]
    public async Task Start_NoAck_RepeatsToCapThenUnacknowledged_SynthesizesOnce()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, "kitchen-01");
        var (pump, plays) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 3 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => plays() >= 1, TimeSpan.FromSeconds(5)); // round 1
        time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => plays() >= 2, TimeSpan.FromSeconds(5)); // round 2
        time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => plays() >= 3, TimeSpan.FromSeconds(5)); // round 3 (== cap)

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmUnacknowledged),
            TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        plays().ShouldBe(3); // no 4th round after the cap

        h.Tts.Verify(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once); // synthesized once, replayed across rounds

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }

    [Fact]
    public async Task Acknowledge_StopsLoopAndMarksAcknowledged()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, "kitchen-01");
        var (pump, plays) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 10 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => plays() >= 1, TimeSpan.FromSeconds(5));

        h.Alerts.Acknowledge("kitchen-01").ShouldNotBeEmpty();

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmAcknowledged),
            TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(120));
        await Task.Delay(50);
        plays().ShouldBe(1); // acknowledged before the second round

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }
}