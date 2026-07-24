using System.Buffers.Binary;

namespace McpChannelVoice.Services.Tts;

// Stateful rational resampler for 16-bit LE mono PCM. 24000→22050 reduces to exactly 147/160,
// so phase is tracked in integer units (one input sample = outputRate/gcd units, one output
// sample = inputRate/gcd units) and can never drift. Linear interpolation between the previous
// and current input sample; both the previous sample and the fractional phase survive across
// Process calls, so chunk boundaries introduce no discontinuities (the click regression).
// One instance per audio stream — state is per-utterance, never share across streams/threads.
public sealed class PcmStreamResampler
{
    private readonly int _phasePerInput;
    private readonly int _phasePerOutput;
    private int _phase;
    private short _prev;
    private bool _hasPrev;

    public PcmStreamResampler(int inputRateHz, int outputRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRateHz);
        var gcd = (int)System.Numerics.BigInteger.GreatestCommonDivisor(inputRateHz, outputRateHz);
        _phasePerInput = outputRateHz / gcd;
        _phasePerOutput = inputRateHz / gcd;
    }

    public byte[] Process(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0)
        {
            throw new ArgumentException("PCM input must contain whole 16-bit samples", nameof(pcm));
        }

        var output = new byte[(pcm.Length / 2 * _phasePerInput / _phasePerOutput + 2) * 2];
        var written = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var cur = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            if (!_hasPrev)
            {
                _prev = cur;
                _hasPrev = true;
                continue;
            }
            while (_phase < _phasePerInput)
            {
                var frac = (double)_phase / _phasePerInput;
                var sample = (short)Math.Round(_prev + (cur - _prev) * frac);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(written), sample);
                written += 2;
                _phase += _phasePerOutput;
            }
            _phase -= _phasePerInput;
            _prev = cur;
        }
        return output[..written];
    }
}