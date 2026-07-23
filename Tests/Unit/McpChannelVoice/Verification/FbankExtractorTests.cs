using System.Text.Json;
using McpChannelVoice.Services.Verification;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Verification;

public class FbankExtractorTests
{
    private static byte[] GoldenSignalPcm()
    {
        const int sr = 16_000;
        var pcm = new byte[8000 * 2];
        for (var i = 0; i < 8000; i++)
        {
            var s = 0.3 * Math.Sin(2 * Math.PI * 440 * i / sr)
                    + 0.2 * Math.Sin(2 * Math.PI * 1337 * i / sr + 1.0);
            var v = (short)Math.Round(s * 32767);
            pcm[2 * i] = (byte)(v & 0xFF);
            pcm[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return pcm;
    }

    [Fact]
    public void Extract_GoldenSignal_MatchesKaldiReference()
    {
        var goldenPath = Path.Combine(
            AppContext.BaseDirectory, "Unit", "McpChannelVoice", "Verification", "Fixtures", "fbank-golden.json");
        var golden = JsonSerializer.Deserialize<GoldenFile>(
            File.ReadAllText(goldenPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var frames = new FbankExtractor().Extract(GoldenSignalPcm());

        frames.Length.ShouldBe(golden.Frames.Count);
        for (var f = 0; f < frames.Length; f++)
        {
            frames[f].Length.ShouldBe(80);
            for (var b = 0; b < 80; b++)
            {
                Math.Abs(frames[f][b] - golden.Frames[f][b]).ShouldBeLessThan(0.02f,
                    $"frame {f} bin {b}: got {frames[f][b]}, golden {golden.Frames[f][b]}");
            }
        }
    }

    [Fact]
    public void Extract_ShorterThanOneFrame_ReturnsNoFrames()
    {
        new FbankExtractor().Extract(new byte[300 * 2]).ShouldBeEmpty();
    }

    private sealed record GoldenFile(List<float[]> Frames);
}