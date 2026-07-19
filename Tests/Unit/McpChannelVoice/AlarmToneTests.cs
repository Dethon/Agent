using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class AlarmToneTests
{
    [Fact]
    public void Pcm_AlarmAndTimer_ProduceDistinctPatterns()
    {
        AlarmTone.Pcm(AnnounceKind.Alarm).ShouldNotBe(AlarmTone.Pcm(AnnounceKind.Timer));
    }

    [Fact]
    public void Pcm_IsNonSilentAndBounded()
    {
        var pcm = AlarmTone.Pcm(AnnounceKind.Alarm);

        pcm.Length.ShouldBeGreaterThan(0);
        (pcm.Length % 2).ShouldBe(0); // whole 16-bit samples
        var samples = Enumerable.Range(0, pcm.Length / 2)
            .Select(i => (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)))
            .ToList();
        samples.Max(s => Math.Abs((int)s)).ShouldBeGreaterThan(short.MaxValue / 4); // audible
        samples.Max(s => Math.Abs((int)s)).ShouldBeLessThan(short.MaxValue);        // no clipping
    }

    [Fact]
    public void Chunk_Uses22050MonoS16le()
    {
        var chunk = AlarmTone.Chunk(AnnounceKind.Timer);

        chunk.Format.SampleRateHz.ShouldBe(22_050);
        chunk.Format.SampleWidthBytes.ShouldBe(2);
        chunk.Format.Channels.ShouldBe(1);
    }
}