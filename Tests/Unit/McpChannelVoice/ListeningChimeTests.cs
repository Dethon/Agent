using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ListeningChimeTests
{
    [Fact]
    public void Pcm_Is22k05Mono180ms_AndNotSilent()
    {
        var pcm = ListeningChime.Pcm();

        // 0.18 s * 22050 Hz * 2 bytes = 7938 bytes.
        pcm.Length.ShouldBe(7938);
        pcm.Any(b => b != 0).ShouldBeTrue();
    }

    [Fact]
    public async Task Stream_YieldsOnePlaybackRateChunk()
    {
        var chunks = new List<AudioChunk>();
        await foreach (var c in ListeningChime.Stream())
        {
            chunks.Add(c);
        }

        chunks.Count.ShouldBe(1);
        chunks[0].Format.SampleRateHz.ShouldBe(22050);
        chunks[0].Format.SampleWidthBytes.ShouldBe(2);
        chunks[0].Format.Channels.ShouldBe(1);
        chunks[0].Data.Length.ShouldBe(7938);
    }
}