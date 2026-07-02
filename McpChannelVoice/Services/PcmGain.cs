namespace McpChannelVoice.Services;

// Saturating gain over 16-bit little-endian mono PCM. Factor >= 1 returns the input untouched so
// the fully-ramped rounds replay the original buffer without a copy.
public static class PcmGain
{
    public static ReadOnlyMemory<byte> Apply(ReadOnlyMemory<byte> pcm, double factor)
    {
        if (factor >= 1.0)
        {
            return pcm;
        }

        var src = pcm.Span;
        var dst = new byte[pcm.Length];
        for (var i = 0; i + 1 < src.Length; i += 2)
        {
            var sample = (short)(src[i] | (src[i + 1] << 8));
            var scaled = (short)Math.Clamp((int)Math.Round(sample * factor), short.MinValue, short.MaxValue);
            dst[i] = (byte)(scaled & 0xFF);
            dst[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
        return dst;
    }
}