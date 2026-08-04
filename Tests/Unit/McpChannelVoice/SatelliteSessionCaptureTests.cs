using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The microphone half of the session: what an open capture receives and what a closed one does not.
// Playback moved out to PlaybackQueueTests.
public class SatelliteSessionCaptureTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public async Task MicRouting_RouteAudio_FeedsActiveCaptureOnly()
    {
        var session = MakeSession();
        var loud = new byte[3200];
        for (var i = 0; i < loud.Length; i += 2)
        { loud[i] = 0x40; loud[i + 1] = 0x1F; }
        AudioChunk loudChunk()
        {
            return new() { Data = loud, Format = AudioFormat.WyomingStandard };
        }

        var silent = new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard };

        // No active capture: routing is a safe no-op.
        Should.NotThrow(() => session.RouteAudio(silent));

        var capture = session.OpenCapture(new SilenceGate(
            new AdaptiveLevelTracker(
                clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
                floorWindow: TimeSpan.FromSeconds(3)),
            trailingSilence: TimeSpan.FromMilliseconds(200),
            maxUtterance: TimeSpan.FromMilliseconds(1000),
            minSpeech: TimeSpan.FromMilliseconds(100)));

        // Speech then trailing silence routed through the session must end the active capture.
        session.RouteAudio(silent); // pre-roll gap seeds the floor
        session.RouteAudio(loudChunk());
        session.RouteAudio(loudChunk());
        session.RouteAudio(silent);
        session.RouteAudio(silent);

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        // After close, routing must not reach any capture (no throw, no effect).
        session.CloseCapture();
        Should.NotThrow(() => session.RouteAudio(loudChunk()));
    }
}