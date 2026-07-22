using Domain.DTOs.Voice;

namespace McpChannelVoice.Services.Tse;

// Minimal RIFF codec for the hub's fixed interchange format (16 kHz mono S16LE). Encode is used
// to ship a capture to the tse-extractor sidecar; Decode wraps its reply for the inner STT.
public static class WavCodec
{
    public static byte[] Encode(IReadOnlyList<AudioChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var format = AudioFormat.WyomingStandard;
        var dataLen = chunks.Sum(c => c.Data.Length);
        using var ms = new MemoryStream(44 + dataLen);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataLen);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);                                           // PCM
        w.Write((short)format.Channels);
        w.Write(format.SampleRateHz);
        w.Write(format.SampleRateHz * format.SampleWidthBytes * format.Channels);
        w.Write((short)(format.SampleWidthBytes * format.Channels)); // block align
        w.Write((short)(format.SampleWidthBytes * 8));               // bits/sample
        w.Write("data"u8);
        w.Write(dataLen);
        foreach (var chunk in chunks)
        {
            w.Write(chunk.Data.Span);
        }
        return ms.ToArray();
    }

    public static AudioChunk Decode(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (wav.Length < 44 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE payload");
        }
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.AsSpan(offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            if (size < 0)
            {
                throw new InvalidDataException("sub-chunk declares a negative size");
            }
            if (id.SequenceEqual("fmt "u8))
            {
                ValidateFmtChunk(wav, offset, size);
            }
            if (id.SequenceEqual("data"u8))
            {
                if (offset + 8 + size > wav.Length)
                {
                    throw new InvalidDataException("data sub-chunk overruns payload");
                }
                return new AudioChunk
                {
                    Data = wav.AsMemory(offset + 8, size),
                    Format = AudioFormat.WyomingStandard
                };
            }
            offset += 8 + size + (size & 1); // sub-chunks are word-aligned
        }
        throw new InvalidDataException("no data sub-chunk found");
    }

    private static void ValidateFmtChunk(byte[] wav, int offset, int size)
    {
        if (size < 16 || offset + 8 + 16 > wav.Length)
        {
            throw new InvalidDataException("fmt sub-chunk is smaller than expected");
        }
        var format = AudioFormat.WyomingStandard;
        var audioFormatTag = BitConverter.ToInt16(wav, offset + 8);
        var channels = BitConverter.ToInt16(wav, offset + 10);
        var sampleRateHz = BitConverter.ToInt32(wav, offset + 12);
        var bitsPerSample = BitConverter.ToInt16(wav, offset + 22);
        if (audioFormatTag != 1 || channels != format.Channels || sampleRateHz != format.SampleRateHz || bitsPerSample != format.SampleWidthBytes * 8)
        {
            throw new InvalidDataException("fmt sub-chunk does not describe 16 kHz mono S16LE PCM");
        }
    }
}