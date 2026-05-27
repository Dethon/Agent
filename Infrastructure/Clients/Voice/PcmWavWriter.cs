using System.Buffers.Binary;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Voice;

public static class PcmWavWriter
{
    public static byte[] Encode(ReadOnlySpan<byte> pcm, AudioFormat fmt)
    {
        var wav = new byte[44 + pcm.Length];
        Span<byte> s = wav;

        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)fmt.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], fmt.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], fmt.SampleRateHz * fmt.Channels * fmt.SampleWidthBytes);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(fmt.Channels * fmt.SampleWidthBytes));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], (short)(fmt.SampleWidthBytes * 8));
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);

        return wav;
    }
}