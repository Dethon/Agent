namespace McpChannelVoice.Services.Verification;

// In-place iterative radix-2 FFT. DSP hot path: plain loops by design.
internal static class Fft
{
    public static void Transform(float[] re, float[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            { j ^= bit; }
            j |= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            for (var i = 0; i < n; i += len)
            {
                for (var k = 0; k < len / 2; k++)
                {
                    // Twiddle factor computed directly per index rather than via a
                    // running complex-multiply recurrence: the recurrence accumulates
                    // rounding error across stages that is negligible for flat/random
                    // spectra but becomes large (double-digit % in power) at bins near
                    // a spectral null of a narrowband/tonal signal.
                    var wRe = (float)Math.Cos(ang * k);
                    var wIm = (float)Math.Sin(ang * k);
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = re[i + k + len / 2] * wRe - im[i + k + len / 2] * wIm;
                    var vIm = re[i + k + len / 2] * wIm + im[i + k + len / 2] * wRe;
                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + len / 2] = uRe - vRe;
                    im[i + k + len / 2] = uIm - vIm;
                }
            }
        }
    }
}