using Domain.DTOs.Voice;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Voice;

public class VoiceDtoTests
{
    [Fact]
    public void AudioFormat_DefaultsToWyomingStandard()
    {
        var fmt = AudioFormat.WyomingStandard;
        fmt.SampleRateHz.ShouldBe(16_000);
        fmt.SampleWidthBytes.ShouldBe(2);
        fmt.Channels.ShouldBe(1);
    }

    [Fact]
    public void AudioChunk_Constructs()
    {
        var chunk = new AudioChunk
        {
            Data = new byte[] { 0, 1, 2, 3 },
            Format = AudioFormat.WyomingStandard,
            Timestamp = TimeSpan.FromMilliseconds(100)
        };
        chunk.Data.Length.ShouldBe(4);
        chunk.Format.SampleRateHz.ShouldBe(16_000);
    }

    [Fact]
    public void TranscriptionResult_Constructs()
    {
        var result = new TranscriptionResult
        {
            Text = "hello world",
            Language = "en",
            Confidence = 0.92
        };
        result.Text.ShouldBe("hello world");
        result.Confidence.ShouldBe(0.92);
    }
}