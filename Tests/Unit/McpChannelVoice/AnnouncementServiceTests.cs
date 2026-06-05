using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class AnnouncementServiceTests
{
    private static SatelliteSession MakeSession(string id, string room) =>
        new(id, new SatelliteConfig { Identity = "household", Room = room });

    private static async IAsyncEnumerable<AudioChunk> FakeAudio()
    {
        yield return new AudioChunk
        {
            Data = new byte[16],
            Format = AudioFormat.WyomingStandard
        };
        await Task.Yield();
    }

    private (AnnouncementService Sut, SatelliteSessionRegistry SessionReg) BuildSut(params (string Id, string Room)[] sats)
    {
        var sessions = new SatelliteSessionRegistry();
        foreach (var (id, room) in sats)
        {
            sessions.Register(MakeSession(id, room));
        }

        var registry = new SatelliteRegistry(sats.ToDictionary(
            s => s.Id,
            s => new SatelliteConfig { Identity = "household", Room = s.Room }));

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(FakeAudio());

        var settings = new VoiceSettings();
        var publisher = new Mock<IMetricsPublisher>();
        var sut = new AnnouncementService(registry, sessions, tts.Object, settings, publisher.Object,
            NullLogger<AnnouncementService>.Instance);
        return (sut, sessions);
    }

    [Fact]
    public async Task Announce_BySatelliteId_TargetsOne()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("bedroom-01", "Bedroom"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" },
            CancellationToken.None);

        response.Satellites.Count.ShouldBe(1);
        response.Satellites[0].Id.ShouldBe("kitchen-01");
        response.Satellites[0].Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Announce_ByRoom_TargetsAllInRoom()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("kitchen-02", "Kitchen"), ("bedroom-01", "Bedroom"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { Room = "Kitchen" }, Text = "hi" },
            CancellationToken.None);

        response.Satellites.Select(s => s.Id).ShouldBe(["kitchen-01", "kitchen-02"], ignoreOrder: true);
    }

    [Fact]
    public async Task Announce_BySatelliteIds_TargetsEachListedSatellite()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("bedroom-01", "Bedroom"), ("office-01", "Office"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteIds = ["kitchen-01", "office-01"] }, Text = "hi" },
            CancellationToken.None);

        response.Satellites.Select(s => s.Id).ShouldBe(["kitchen-01", "office-01"], ignoreOrder: true);
    }

    [Fact]
    public async Task Announce_All_TargetsEverySession()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("bedroom-01", "Bedroom"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { All = true }, Text = "hi" },
            CancellationToken.None);

        response.Satellites.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Announce_UnknownTarget_Throws404Equivalent()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"));

        await Should.ThrowAsync<AnnounceTargetNotFoundException>(
            () => sut.AnnounceAsync(
                new AnnounceRequest { Target = new() { SatelliteId = "ghost" }, Text = "hi" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Announce_HighPriority_PreemptsCurrentReply()
    {
        var (sut, sessions) = BuildSut(("kitchen-01", "Kitchen"));
        // Pre-load a long-running playback to be preempted.
        var session = sessions.Get("kitchen-01")!;
        var started = new TaskCompletionSource();
        var preempted = new TaskCompletionSource();
        var pump = session.RunPlaybackLoopAsync((c, ct) => Task.Delay(50, ct), CancellationToken.None);
        await session.EnqueuePlaybackAsync(
            new PlaybackJob("ongoing", AnnouncePriority.Normal,
                NeverEnding(),
                _ => { started.TrySetResult(); return Task.CompletedTask; },
                _ => { preempted.TrySetResult(); return Task.CompletedTask; }),
            queueMaxDepth: 4);

        // Wait until the playback loop has actually picked up the ongoing job
        // before issuing the high-priority preemption (otherwise PreemptCurrent
        // has no current playback to cancel).
        (await Task.WhenAny(started.Task, Task.Delay(2_000))).ShouldBe(started.Task);

        await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "alert", Priority = AnnouncePriority.High },
            CancellationToken.None);

        // Verify the 'ongoing' job's OnPreempted callback fired within a reasonable window.
        (await Task.WhenAny(preempted.Task, Task.Delay(2_000))).ShouldBe(preempted.Task);
    }

    private static async IAsyncEnumerable<AudioChunk> NeverEnding()
    {
        while (true)
        {
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
            await Task.Delay(10);
        }
    }
}