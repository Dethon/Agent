using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class PcmWavWriterTests
{
    [Fact]
    public void WriteWav_ProducesHeaderedFile()
    {
        var pcm = new byte[3200];
        var wav = PcmWavWriter.Encode(pcm, AudioFormat.WyomingStandard);

        wav[..4].ShouldBe("RIFF"u8.ToArray());
        wav[8..12].ShouldBe("WAVE"u8.ToArray());
        wav.Length.ShouldBe(pcm.Length + 44);
    }
}