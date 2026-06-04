using Domain.DTOs.Voice;

namespace Tests.Integration.McpChannelVoice;

// Reads a 16 kHz / 16-bit / mono PCM WAV and slices it into ~100 ms AudioChunks.
internal static class WavChunks
{
    public static IReadOnlyList<AudioChunk> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var dataStart = FindDataChunk(bytes);
        const int frameBytes = 3200; // 100 ms @ 16 kHz/16-bit/mono
        var chunks = new List<AudioChunk>();
        for (var offset = dataStart; offset < bytes.Length; offset += frameBytes)
        {
            var len = Math.Min(frameBytes, bytes.Length - offset);
            chunks.Add(new AudioChunk
            {
                Data = bytes.AsMemory(offset, len),
                Format = AudioFormat.WyomingStandard,
                Timestamp = TimeSpan.Zero
            });
        }
        return chunks;
    }

    private static int FindDataChunk(byte[] bytes)
    {
        var i = 12;
        while (i + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, i, 4);
            var size = BitConverter.ToInt32(bytes, i + 4);
            if (id == "data")
            {
                return i + 8;
            }
            i += 8 + size;
        }
        return 44; // canonical PCM header fallback
    }
}