using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

// Accuracy gate. Requires a reachable wyoming-whisper (set WYOMING_WHISPER_HOST/PORT)
// and a corpus of <name>.wav + <name>.txt under SEGMENTED_STT_CORPUS. No corpus or
// no host => the test no-ops, so CI without the rig stays green.
public class SegmentedSttAccuracyTests
{
    [Fact]
    public async Task SegmentedWer_DoesNotExceedBatchWer()
    {
        var corpus = Environment.GetEnvironmentVariable("SEGMENTED_STT_CORPUS");
        var host = Environment.GetEnvironmentVariable("WYOMING_WHISPER_HOST");
        if (string.IsNullOrWhiteSpace(corpus) || !Directory.Exists(corpus) || string.IsNullOrWhiteSpace(host))
        {
            return; // rig not provisioned — skip
        }

        var port = int.TryParse(Environment.GetEnvironmentVariable("WYOMING_WHISPER_PORT"), out var p) ? p : 10300;
        var wyomingConfig = new WyomingSttConfig { Host = host, Port = port, Language = "es" };
        ISpeechToText batch = new WyomingSpeechToText(wyomingConfig, NullLogger<WyomingSpeechToText>.Instance);
        var segmented = new SegmentedSpeechToText(
            new WyomingSpeechToText(wyomingConfig, NullLogger<WyomingSpeechToText>.Instance),
            new SegmentedSttConfig { Enabled = true },
            NullLogger<SegmentedSpeechToText>.Instance);

        var clips = Directory.GetFiles(corpus, "*.wav");
        clips.Length.ShouldBeGreaterThan(0, "corpus directory has no .wav clips");

        double batchTotal = 0, segTotal = 0;
        foreach (var wav in clips)
        {
            var reference = await File.ReadAllTextAsync(Path.ChangeExtension(wav, ".txt"));
            var chunks = WavChunks.Read(wav);

            var batchText = (await batch.TranscribeAsync(Replay(chunks), new TranscriptionOptions(), default)).Text;
            var segText = (await segmented.TranscribeAsync(Replay(chunks), new TranscriptionOptions(), default)).Text;

            batchTotal += WordErrorRate.Compute(reference, batchText);
            segTotal += WordErrorRate.Compute(reference, segText);
        }

        var batchWer = batchTotal / clips.Length;
        var segWer = segTotal / clips.Length;

        // Segmented must not regress beyond a 1% absolute epsilon.
        segWer.ShouldBeLessThanOrEqualTo(batchWer + 0.01);
    }

    private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
        await Task.CompletedTask;
    }
}