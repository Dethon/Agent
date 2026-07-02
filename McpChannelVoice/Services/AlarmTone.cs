using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// Generated alarm/timer earcons — 22.05 kHz mono S16LE like ListeningChime (the satellite sink is
// fixed at that rate), no asset files. The alarm is an urgent low/high pulse train; the timer is a
// faster, higher triple-beep so the two are audibly distinct.
public static class AlarmTone
{
    private const int SampleRateHz = 22_050;
    private const double Amplitude = 0.5;

    private static readonly AudioFormat _playbackFormat = new()
    {
        SampleRateHz = SampleRateHz,
        SampleWidthBytes = 2,
        Channels = 1
    };

    public static byte[] Pcm(AnnounceKind kind) => kind == AnnounceKind.Timer
        ? Pattern([(1320, 0.09), (0, 0.05), (1320, 0.09), (0, 0.05), (1760, 0.16)])
        : Pattern([(880, 0.14), (0, 0.06), (660, 0.14), (0, 0.06), (880, 0.14), (0, 0.06), (660, 0.18)]);

    public static AudioChunk Chunk(AnnounceKind kind) => new() { Data = Pcm(kind), Format = _playbackFormat };

    // Frequency 0 renders silence. Every voiced segment gets a 10 ms fade in/out to avoid clicks.
    private static byte[] Pattern(IReadOnlyList<(double Freq, double Seconds)> segments)
    {
        var samples = segments
            .SelectMany(seg => Segment(seg.Freq, (int)(SampleRateHz * seg.Seconds)))
            .ToList();
        var pcm = new byte[samples.Count * 2];
        foreach (var (s16, i) in samples.Select((s, i) => (s, i)))
        {
            pcm[i * 2] = (byte)(s16 & 0xFF);
            pcm[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
        }
        return pcm;
    }

    private static IEnumerable<short> Segment(double freq, int count)
    {
        var fadeSamples = SampleRateHz * 0.01;
        return Enumerable.Range(0, count).Select(i =>
        {
            if (freq <= 0)
            {
                return (short)0;
            }
            var fade = Math.Min(1.0, Math.Min(i, count - i) / fadeSamples);
            var value = Math.Sin(2 * Math.PI * freq * i / SampleRateHz) * fade * Amplitude;
            return (short)(value * short.MaxValue);
        });
    }
}