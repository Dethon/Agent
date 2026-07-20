namespace McpChannelVoice.Services.WyomingProtocol;

// Server-side end-of-utterance detection for local-wake-word satellites.
//
// A WakeStreamingSatellite streams mic audio open-endedly after the wake word
// fires and only stops when it receives a Transcript back. There is no
// audio-stop to lean on, so the hub must decide when the speaker has finished:
// once speech has been observed, a run of trailing silence ends the utterance.
// What counts as speech vs silence is delegated to AdaptiveLevelTracker so a
// noisy room (TV, ducked music) raises the bar instead of pinning the capture
// open. A max-utterance cap bounds runaway streams; speech shorter than
// minSpeech is treated as noise and never ends the turn on its own.
public sealed class SilenceGate(
    AdaptiveLevelTracker tracker,
    TimeSpan trailingSilence,
    TimeSpan maxUtterance,
    TimeSpan minSpeech,
    TimeSpan noSpeechTimeout = default)
{
    private TimeSpan _elapsed;
    private TimeSpan _speechElapsed;
    private TimeSpan _trailingSilence;
    private double _trailingEnergyMs;
    private bool _speechStarted;
    private double _peakRms;

    public enum Decision
    {
        Continue,
        EndUtterance,
        NoSpeech
    }

    public TimeSpan SpeechElapsed => _speechElapsed;

    public double PeakRms => _peakRms;

    public double FloorRms => tracker.FloorRms;

    public string? EndReason { get; private set; }

    public Decision Process(ReadOnlySpan<byte> pcm, int sampleRateHz, int sampleWidthBytes, int channels)
    {
        var duration = DurationOf(pcm.Length, sampleRateHz, sampleWidthBytes, channels);
        _elapsed += duration;

        var rms = Rms(pcm, sampleWidthBytes);
        _peakRms = Math.Max(_peakRms, rms);

        if (tracker.IsSpeech(rms, duration.TotalMilliseconds))
        {
            _speechStarted = true;
            _speechElapsed += duration;
            _trailingSilence = TimeSpan.Zero;
            _trailingEnergyMs = 0;
        }
        else if (_speechStarted)
        {
            _trailingSilence += duration;
            _trailingEnergyMs += rms * rms * duration.TotalMilliseconds;
            if (_speechElapsed > minSpeech && _trailingSilence >= trailingSilence)
            {
                // A floor seeded during a background lull lets resumed TV latch as speech
                // until the min-window converges; the capture then ends here full of TV
                // audio. Such pseudo-speech never stands above the trailing background it
                // decays into, while real speech sits an entry margin (or more) over it —
                // so demote the capture to no-speech instead of dispatching background.
                // Only gates with a no-speech window may emit NoSpeech (the segmenting
                // gate inside SegmentedSpeechToText must keep slicing on EndUtterance).
                if (noSpeechTimeout > TimeSpan.Zero && !tracker.SpeechProminentOver(TrailingDb()))
                {
                    EndReason = "no_speech";
                    return Decision.NoSpeech;
                }
                EndReason = "trailing_silence";
                return Decision.EndUtterance;
            }
        }

        // The no-speech window expires unless MEANINGFUL speech (> minSpeech) has begun. Gating on
        // _speechElapsed rather than _speechStarted is deliberate: a sub-minSpeech blip (echo tail,
        // a cough) is noise by this gate's own definition and must not latch the window shut — else
        // the capture would hang open until the maxUtterance cap instead of timing out here.
        if (_speechElapsed <= minSpeech && noSpeechTimeout > TimeSpan.Zero && _elapsed >= noSpeechTimeout)
        {
            EndReason = "no_speech";
            return Decision.NoSpeech;
        }

        if (_elapsed >= maxUtterance)
        {
            EndReason = "max_utterance";
            return Decision.EndUtterance;
        }
        return Decision.Continue;
    }

    // Deliberately does NOT reset the tracker: SegmentedSpeechToText resets the gate per
    // phrase segment, and the learned noise floor must survive segment boundaries.
    public void Reset()
    {
        _elapsed = TimeSpan.Zero;
        _speechElapsed = TimeSpan.Zero;
        _trailingSilence = TimeSpan.Zero;
        _trailingEnergyMs = 0;
        _speechStarted = false;
        _peakRms = 0;
        EndReason = null;
    }

    private double TrailingDb() =>
        10 * Math.Log10(Math.Max(_trailingEnergyMs / _trailingSilence.TotalMilliseconds, 1));

    private static TimeSpan DurationOf(int byteCount, int sampleRateHz, int sampleWidthBytes, int channels)
    {
        var bytesPerSecond = sampleRateHz * sampleWidthBytes * channels;
        return bytesPerSecond == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)byteCount / bytesPerSecond);
    }

    private static double Rms(ReadOnlySpan<byte> pcm, int sampleWidthBytes)
    {
        if (sampleWidthBytes != 2 || pcm.Length < 2)
        {
            return 0;
        }

        var samples = pcm.Length / 2;
        double sumSquares = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / samples);
    }
}