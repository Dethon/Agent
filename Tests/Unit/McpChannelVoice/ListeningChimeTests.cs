using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ListeningChimeTests
{
    [Fact]
    public void Pcm_Is16kMono180ms_AndNotSilent()
    {
        var pcm = ListeningChime.Pcm();

        // 0.18 s * 16000 Hz * 2 bytes = 5760 bytes.
        pcm.Length.ShouldBe(5760);
        pcm.Any(b => b != 0).ShouldBeTrue();
    }

    [Fact]
    public async Task Stream_YieldsOneWyomingStandardChunk()
    {
        var chunks = new List<AudioChunk>();
        await foreach (var c in ListeningChime.Stream())
        {
            chunks.Add(c);
        }

        chunks.Count.ShouldBe(1);
        chunks[0].Format.ShouldBe(AudioFormat.WyomingStandard);
        chunks[0].Data.Length.ShouldBe(5760);
    }
}