using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class SilenceGateTests
{
    // 16 kHz, 16-bit, mono => 2 bytes/sample => 3200 bytes == 100 ms.
    private const int Rate = 16_000;
    private const int Width = 2;
    private const int Channels = 1;
    private const int ChunkBytes = 3200;

    private static byte[] Loud()
    {
        var pcm = new byte[ChunkBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            // Int16 value 8000 (little-endian) => RMS well above the threshold.
            pcm[i] = 0x40;
            pcm[i + 1] = 0x1F;
        }
        return pcm;
    }

    private static byte[] Silent() => new byte[ChunkBytes];

    private static SilenceGate NewGate() => new(
        rmsThreshold: 500,
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(2000),
        minSpeech: TimeSpan.FromMilliseconds(100));

    private static SilenceGate.Decision Feed(SilenceGate gate, byte[] pcm) =>
        gate.Process(pcm, Rate, Width, Channels);

    [Fact]
    public void Process_TrailingSilenceAfterSpeech_EndsUtterance()
    {
        var gate = NewGate();

        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.EndUtterance);
    }

    [Fact]
    public void Process_SilenceBeforeSpeech_DoesNotEnd()
    {
        var gate = NewGate();

        foreach (var _ in Enumerable.Range(0, 5))
        {
            Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        }
    }

    [Fact]
    public void Process_BriefPauseBetweenSpeech_DoesNotEnd()
    {
        var gate = NewGate();

        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        // Only one silent chunk since the last speech => trailing silence not yet reached.
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
    }

    [Fact]
    public void Process_ExceedsMaxUtterance_EndsEvenWhileSpeaking()
    {
        var gate = NewGate();

        // 2000 ms cap / 100 ms per chunk => the 20th chunk crosses the cap.
        var decisions = Enumerable.Range(0, 20).Select(_ => Feed(gate, Loud())).ToList();

        decisions.Take(19).ShouldAllBe(d => d == SilenceGate.Decision.Continue);
        decisions[^1].ShouldBe(SilenceGate.Decision.EndUtterance);
    }

    [Fact]
    public void Process_OnlyBlipOfSpeechThenSilence_WaitsForMaxUtterance()
    {
        var gate = NewGate();

        // One loud chunk = 100 ms which does NOT meet the 100 ms-strict minSpeech
        // gate on its own (needs to exceed), so trailing silence must not end early.
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
    }

    [Fact]
    public void SpeechElapsed_AccumulatesSpeechAndIgnoresSilence()
    {
        var gate = NewGate();

        Feed(gate, Loud());   // 100 ms speech
        Feed(gate, Loud());   // 100 ms speech
        Feed(gate, Silent()); // silence — must not count

        gate.SpeechElapsed.ShouldBe(TimeSpan.FromMilliseconds(200));
    }

    private static SilenceGate FollowUpGate() => new(
        rmsThreshold: 500,
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(10_000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(500));

    [Fact]
    public void Process_NoSpeechWithinWindow_ReturnsNoSpeech()
    {
        var gate = FollowUpGate();

        // 500 ms window / 100 ms per chunk => the 5th silent chunk crosses it.
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.NoSpeech);
    }

    [Fact]
    public void Process_SpeechBeforeWindowExpires_DoesNotReturnNoSpeech()
    {
        var gate = FollowUpGate();

        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);   // speech starts
        // Keep feeding past the no-speech window: speech started, so NoSpeech must never fire.
        foreach (var _ in Enumerable.Range(0, 8))
        {
            Feed(gate, Loud()).ShouldNotBe(SilenceGate.Decision.NoSpeech);
        }
    }

    [Fact]
    public void Process_SubMinSpeechBlipThenSilence_StillTimesOutAsNoSpeech()
    {
        var gate = FollowUpGate();

        // A single 100 ms loud chunk does NOT exceed the 100 ms minSpeech gate, so it is noise.
        // A noise blip (echo tail, a cough) must NOT disable the no-speech window — otherwise the
        // capture hangs open until the maxUtterance cap. The window must still expire as NoSpeech.
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.NoSpeech);
    }

    [Fact]
    public void Process_NoSpeechTimeoutDisabledByDefault_NeverReturnsNoSpeech()
    {
        var gate = NewGate(); // default gate has noSpeechTimeout = default (disabled)

        foreach (var _ in Enumerable.Range(0, 30))
        {
            Feed(gate, Silent()).ShouldNotBe(SilenceGate.Decision.NoSpeech);
        }
    }

    [Fact]
    public void PeakRms_TracksLoudestChunkSeen()
    {
        var gate = NewGate();

        Feed(gate, Silent());
        Feed(gate, Loud());
        Feed(gate, Silent());

        gate.PeakRms.ShouldBe(8000, 1.0);
    }

    [Fact]
    public void PeakRms_Reset_ClearsIt()
    {
        var gate = NewGate();
        Feed(gate, Loud());

        gate.Reset();

        gate.PeakRms.ShouldBe(0);
    }
}