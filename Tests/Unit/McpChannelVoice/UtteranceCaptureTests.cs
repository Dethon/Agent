using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class UtteranceCaptureTests
{
    private const int Bytes = 3200; // 100 ms at 16 kHz/16-bit mono

    private static AudioChunk Loud()
    {
        var pcm = new byte[Bytes];
        for (var i = 0; i < pcm.Length; i += 2)
        { pcm[i] = 0x40; pcm[i + 1] = 0x1F; }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    private static AudioChunk Silent() =>
        new() { Data = new byte[Bytes], Format = AudioFormat.WyomingStandard };

    private static SilenceGate Gate(int noSpeechMs = 0) => new(
        new AdaptiveLevelTracker(
            clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
            floorWindow: TimeSpan.FromSeconds(3)),
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(5000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(noSpeechMs));

    [Fact]
    public async Task Feed_SpeechThenSilence_CompletesEndedAndExposesAudio()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        var count = 0;
        await foreach (var _ in capture.Audio)
        { count++; }
        count.ShouldBe(5);
    }

    [Fact]
    public async Task Feed_OnlySilenceWithinWindow_CompletesNoSpeech()
    {
        var capture = new UtteranceCapture(Gate(noSpeechMs: 300));

        capture.Feed(Silent());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.NoSpeech);
    }

    [Fact]
    public async Task ForceEnd_CompletesEnded()
    {
        var capture = new UtteranceCapture(Gate());
        capture.Feed(Loud());
        capture.ForceEnd();
        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
    }

    [Fact]
    public async Task Stats_AfterEndedCapture_ReportsPeakRmsAndSpeechMs()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.PeakRms.ShouldBe(8000, 1.0);
        capture.Stats.SpeechMs.ShouldBe(200);
    }
}