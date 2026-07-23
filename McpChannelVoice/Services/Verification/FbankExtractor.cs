namespace McpChannelVoice.Services.Verification;

// Kaldi-compatible 80-dim log-mel filterbank (25 ms window / 10 ms shift, 16 kHz,
// dither 0, snip-edges, Povey window, int16-scale input). Matches what the
// WeSpeaker/CAM++ ONNX speaker models were trained on; verified against
// kaldi-native-fbank golden vectors. The int16-scale input convention is
// load-bearing: the CAM++ model consumes these log-mel values directly with no
// per-utterance mean subtraction anywhere in the pipeline, and it is scale-sensitive,
// so switching to float [-1,1] input would perturb every bin the model sees.
// Confirmed empirically against the sherpa-onnx reference extractor.
// DSP hot path: plain loops by design.
public sealed class FbankExtractor
{
    private const int SampleRate = 16_000;
    private const int FrameLength = 400;
    private const int FrameShift = 160;
    private const int FftSize = 512;
    private const int NumBins = 80;
    private const float PreEmphasis = 0.97f;
    private const double LowFreqHz = 20;
    private const double HighFreqHz = SampleRate / 2.0;

    private readonly float[] _window = BuildPoveyWindow();
    private readonly (int Offset, float[] Weights)[] _mel = BuildMelBanks();

    public float[][] Extract(ReadOnlySpan<byte> pcmS16Le)
    {
        var sampleCount = pcmS16Le.Length / 2;
        if (sampleCount < FrameLength)
        {
            return [];
        }

        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = (short)(pcmS16Le[2 * i] | (pcmS16Le[2 * i + 1] << 8));
        }

        var frameCount = (sampleCount - FrameLength) / FrameShift + 1;
        var frames = new float[frameCount][];
        var re = new float[FftSize];
        var im = new float[FftSize];
        var power = new float[FftSize / 2 + 1];

        for (var f = 0; f < frameCount; f++)
        {
            Array.Clear(re);
            Array.Clear(im);
            var start = f * FrameShift;

            var mean = 0f;
            for (var i = 0; i < FrameLength; i++)
            { mean += samples[start + i]; }
            mean /= FrameLength;

            for (var i = 0; i < FrameLength; i++)
            { re[i] = samples[start + i] - mean; }

            for (var i = FrameLength - 1; i > 0; i--)
            { re[i] -= PreEmphasis * re[i - 1]; }
            re[0] -= PreEmphasis * re[0];

            for (var i = 0; i < FrameLength; i++)
            { re[i] *= _window[i]; }

            Fft.Transform(re, im);
            for (var k = 0; k <= FftSize / 2; k++)
            { power[k] = re[k] * re[k] + im[k] * im[k]; }

            var bins = new float[NumBins];
            for (var b = 0; b < NumBins; b++)
            {
                var (offset, weights) = _mel[b];
                var energy = 0f;
                for (var k = 0; k < weights.Length; k++)
                { energy += weights[k] * power[offset + k]; }
                bins[b] = MathF.Log(MathF.Max(energy, float.Epsilon));
            }
            frames[f] = bins;
        }
        return frames;
    }

    private static float[] BuildPoveyWindow()
    {
        var window = new float[FrameLength];
        for (var i = 0; i < FrameLength; i++)
        {
            var hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameLength - 1));
            window[i] = (float)Math.Pow(hann, 0.85);
        }
        return window;
    }

    private static double MelScale(double hz) => 1127.0 * Math.Log(1.0 + hz / 700.0);

    private static (int Offset, float[] Weights)[] BuildMelBanks()
    {
        var melLow = MelScale(LowFreqHz);
        var melHigh = MelScale(HighFreqHz);
        var banks = new (int, float[])[NumBins];
        var binHz = (double)SampleRate / FftSize;
        var melFreqDelta = (melHigh - melLow) / (NumBins + 1);

        for (var b = 0; b < NumBins; b++)
        {
            var left = melLow + b * melFreqDelta;
            var center = melLow + (b + 1) * melFreqDelta;
            var right = melLow + (b + 2) * melFreqDelta;

            var weights = new List<float>();
            var offset = -1;
            // Kaldi's mel-bank construction only ever considers FFT bins 0..FftSize/2-1
            // (it never assigns the Nyquist bin to any triangle), even though the power
            // spectrum itself is computed one bin further (see Extract below).
            for (var k = 0; k < FftSize / 2; k++)
            {
                var mel = MelScale(k * binHz);
                var weight = 0.0;
                if (mel > left && mel < right)
                {
                    weight = mel <= center
                        ? (mel - left) / (center - left)
                        : (right - mel) / (right - center);
                }
                if (weight > 0)
                {
                    if (offset < 0)
                    { offset = k; }
                    weights.Add((float)weight);
                }
                else if (offset >= 0)
                {
                    break;
                }
            }
            banks[b] = (Math.Max(offset, 0), weights.ToArray());
        }
        return banks;
    }
}