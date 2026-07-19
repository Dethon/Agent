using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class PcmGainTests
{
    private static byte[] Pcm(params short[] samples) =>
        samples.SelectMany(s => new[] { (byte)(s & 0xFF), (byte)((s >> 8) & 0xFF) }).ToArray();

    private static short[] Samples(ReadOnlyMemory<byte> pcm) =>
        Enumerable.Range(0, pcm.Length / 2)
            .Select(i => (short)(pcm.Span[i * 2] | (pcm.Span[i * 2 + 1] << 8)))
            .ToArray();

    [Fact]
    public void Apply_HalfGain_HalvesSamples()
    {
        var scaled = PcmGain.Apply(Pcm(1000, -1000, 0), 0.5);

        Samples(scaled).ShouldBe(new short[] { 500, -500, 0 });
    }

    [Fact]
    public void Apply_FullGain_ReturnsInputUnchanged()
    {
        var input = Pcm(1000, -1000);

        PcmGain.Apply(input, 1.0).ToArray().ShouldBe(input);
    }

    [Fact]
    public void Apply_NeverOverflows()
    {
        var scaled = PcmGain.Apply(Pcm(short.MaxValue, short.MinValue), 0.99);

        var samples = Samples(scaled);
        samples[0].ShouldBeGreaterThan((short)0);
        samples[1].ShouldBeLessThan((short)0);
    }
}