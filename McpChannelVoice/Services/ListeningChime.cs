using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// A short rising two-tone earcon played before a wake-free follow-up window so the
// user knows the mic is open. Generated PCM (22.05 kHz/16-bit mono) — no asset file.
public static class ListeningChime
{
    private const double DurationSeconds = 0.18;

    // Satellites play hub audio through fixed 22050 Hz sinks (aplay/paplay -r 22050) and
    // ignore the announced rate, so the earcon must be generated at the sink rate or it
    // plays ~1.4x fast.
    private static readonly AudioFormat _playbackFormat = new()
    {
        SampleRateHz = 22_050,
        SampleWidthBytes = 2,
        Channels = 1
    };

    public static byte[] Pcm(int sampleRateHz = 22_050)
    {
        var samples = (int)(sampleRateHz * DurationSeconds);
        var pcm = new byte[samples * 2];
        var fadeSamples = sampleRateHz * 0.01; // 10 ms in/out fade

        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / sampleRateHz;
            var freq = t < DurationSeconds / 2 ? 660.0 : 990.0;
            var fade = Math.Min(1.0, Math.Min(i, samples - i) / fadeSamples);
            var value = Math.Sin(2 * Math.PI * freq * t) * fade * 0.35;
            var s16 = (short)(value * short.MaxValue);
            pcm[i * 2] = (byte)(s16 & 0xFF);
            pcm[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
        }

        return pcm;
    }

    public static async IAsyncEnumerable<AudioChunk> Stream()
    {
        yield return new AudioChunk { Data = Pcm(), Format = _playbackFormat };
        await Task.CompletedTask;
    }
}