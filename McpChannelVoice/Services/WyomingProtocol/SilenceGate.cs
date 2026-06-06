namespace McpChannelVoice.Services.WyomingProtocol;

// Server-side end-of-utterance detection for local-wake-word satellites.
//
// A WakeStreamingSatellite streams mic audio open-endedly after the wake word
// fires and only stops when it receives a Transcript back. There is no
// audio-stop to lean on, so the hub must decide when the speaker has finished:
// once speech has been observed, a run of trailing silence ends the utterance.
// A max-utterance cap bounds runaway streams; speech shorter than minSpeech is
// treated as noise and never ends the turn on its own.
public sealed class SilenceGate(
    double rmsThreshold,
    TimeSpan trailingSilence,
    TimeSpan maxUtterance,
    TimeSpan minSpeech,
    TimeSpan noSpeechTimeout = default)
{
    private TimeSpan _elapsed;
    private TimeSpan _speechElapsed;
    private TimeSpan _trailingSilence;
    private bool _speechStarted;

    public enum Decision
    {
        Continue,
        EndUtterance,
        NoSpeech
    }

    public TimeSpan SpeechElapsed => _speechElapsed;

    public Decision Process(ReadOnlySpan<byte> pcm, int sampleRateHz, int sampleWidthBytes, int channels)
    {
        var duration = DurationOf(pcm.Length, sampleRateHz, sampleWidthBytes, channels);
        _elapsed += duration;

        if (Rms(pcm, sampleWidthBytes) >= rmsThreshold)
        {
            _speechStarted = true;
            _speechElapsed += duration;
            _trailingSilence = TimeSpan.Zero;
        }
        else if (_speechStarted)
        {
            _trailingSilence += duration;
            if (_speechElapsed > minSpeech && _trailingSilence >= trailingSilence)
            {
                return Decision.EndUtterance;
            }
        }
        else if (noSpeechTimeout > TimeSpan.Zero && _elapsed >= noSpeechTimeout)
        {
            return Decision.NoSpeech;
        }

        return _elapsed >= maxUtterance ? Decision.EndUtterance : Decision.Continue;
    }

    public void Reset()
    {
        _elapsed = TimeSpan.Zero;
        _speechElapsed = TimeSpan.Zero;
        _trailingSilence = TimeSpan.Zero;
        _speechStarted = false;
    }

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