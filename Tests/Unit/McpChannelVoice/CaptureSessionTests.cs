using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// Opening a turn comes in two named shapes now. A wake turn carries what the satellite reported
// about the wake that opened it; a follow-up turn has no wake of its own and takes nothing. The
// split is what deleted the stash the announcement used to travel through, and with it the rule
// that said a wake nobody consumed had to be dropped by hand.
public class CaptureSessionTests
{
    private static readonly SatelliteConfig _config = new() { Identity = "household", Room = "Kitchen" };

    private sealed class Harness
    {
        public readonly List<WakeAnnouncement?> Opened = [];
        public readonly SatelliteSession Session = new("kitchen-01", _config);
        public readonly SilenceGateFactory Gates;
        public readonly CaptureSession Sut;

        public Harness()
        {
            // Above every level fed below, so nothing latches as speech and the floor keeps
            // measuring — the case where an uncapped floor drifts up to the background.
            Gates = new SilenceGateFactory(
                new VoiceSettings { FollowUp = new FollowUpSettings { WindowMs = 2000 } },
                new WyomingClientSettings { SilenceRmsThreshold = 20_000 },
                new FakeTimeProvider(DateTimeOffset.UtcNow));
            Sut = new CaptureSession(
                Session, Gates, TimeProvider.System, TimeSpan.FromSeconds(5),
                onWakeTurn: Opened.Add);
        }
    }

    // Constant-amplitude S16LE: for a flat signal the RMS is the amplitude itself. 3200 bytes =
    // 100 ms at 16 kHz mono.
    private static void Feed(UtteranceCapture capture, short amplitude, int times)
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(amplitude & 0xFF);
            pcm[i + 1] = (byte)(amplitude >> 8);
        }
        var chunk = new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
        foreach (var _ in Enumerable.Range(0, times))
        {
            capture.Feed(chunk);
        }
    }

    [Fact]
    public void OpenWakeTurn_ReportedRoomLevel_CapsTheGateThisTurnRunsOn()
    {
        // The recording has to land BEFORE the gate is built, or the turn it was measured for runs
        // uncapped and only the next one benefits. Asserting on this capture's own floor is what
        // pins the order.
        var h = new Harness();

        var capture = h.Sut.OpenWakeTurn(new WakeAnnouncement(9000, 0.87, "wake", RoomRms: 100));
        Feed(capture, 800, times: 10);

        capture.Stats.FloorRms.ShouldBe(100, tolerance: 1);
        capture.Stats.MeasuredFloorRms.ShouldBe(800, tolerance: 20);
    }

    // A legacy or foreign satellite announces its mic stream with audio-start and sends no wake
    // metadata at all. The zero recorded for it must read as "no measurement", not as "the room is
    // silent" — pinning the floor at silence arms the adaptive regime and endpoints people
    // mid-sentence.
    [Fact]
    public void OpenWakeTurn_NoAnnouncement_LeavesTheFloorWhereTheCaptureMeasuresIt()
    {
        var h = new Harness();

        var capture = h.Sut.OpenWakeTurn(null);
        Feed(capture, 800, times: 10);

        capture.Stats.FloorRms.ShouldBe(800, tolerance: 20);
    }

    [Fact]
    public void OpenWakeTurn_PassesTheAnnouncementStraightToTheHook()
    {
        var h = new Harness();
        var announcement = new WakeAnnouncement(1234.5, 0.87, "wake", RoomRms: 68.25);

        h.Sut.OpenWakeTurn(announcement);

        h.Opened.ShouldHaveSingleItem().ShouldBe(announcement);
    }

    // The hook is where WakeTriggered is published. A follow-up has no wake, so reaching it here
    // would report the wake turn's loudness against a turn that never had one — the exact
    // misattribution the stash used to make possible.
    [Fact]
    public void OpenFollowUpTurn_NeverReachesTheWakeHook()
    {
        var h = new Harness();

        h.Sut.OpenFollowUpTurn();

        h.Opened.ShouldBeEmpty();
        h.Session.HasActiveCapture.ShouldBeTrue();
    }
}