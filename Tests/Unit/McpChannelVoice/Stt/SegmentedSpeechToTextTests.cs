using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class SegmentedSpeechToTextTests
{
    private const int ChunkBytes = 3200; // 100 ms @ 16 kHz/16-bit/mono

    private static byte[] LoudPcm()
    {
        var pcm = new byte[ChunkBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = 0x40;     // Int16 8000 little-endian => RMS >> 500
            pcm[i + 1] = 0x1F;
        }
        return pcm;
    }

    private static AudioChunk Loud() => new()
    { Data = LoudPcm(), Format = AudioFormat.WyomingStandard, Timestamp = TimeSpan.Zero };

    private static AudioChunk Silent() => new()
    { Data = new byte[ChunkBytes], Format = AudioFormat.WyomingStandard, Timestamp = TimeSpan.Zero };

    private static IEnumerable<AudioChunk> Speech(int chunks) => Enumerable.Range(0, chunks).Select(_ => Loud());
    private static IEnumerable<AudioChunk> Silence(int chunks) => Enumerable.Range(0, chunks).Select(_ => Silent());

    private static async IAsyncEnumerable<AudioChunk> Stream(params IEnumerable<AudioChunk>[] parts)
    {
        foreach (var chunk in parts.SelectMany(p => p))
        {
            yield return chunk;
            await Task.Yield();
        }
    }

    // 100 ms chunks: 300 ms segment-silence => 3 silent chunks close a segment;
    // 500 ms min-segment => 5 loud chunks minimum.
    private static SegmentedSttConfig Config(int maxInFlight = 1) => new()
    {
        Enabled = true,
        SilenceRmsThreshold = 500,
        SegmentSilenceMs = 300,
        MinSegmentMs = 500,
        MaxInFlightDecodes = maxInFlight
    };

    private static SegmentedSpeechToText New(ISpeechToText inner, SegmentedSttConfig? config = null) =>
        new(inner, config ?? Config(), NullLogger<SegmentedSpeechToText>.Instance);

    // Inner stub: returns the chunk count it received as text, optionally via a custom handler.
    private sealed class FakeStt(Func<int, Task<TranscriptionResult>>? handler = null) : ISpeechToText
    {
        private readonly Lock _lock = new();
        private int _concurrent;
        public int MaxConcurrent { get; private set; }
        public int Calls { get; private set; }

        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            lock (_lock)
            { _concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, _concurrent); Calls++; }
            try
            {
                var count = 0;
                await foreach (var _ in audio.WithCancellation(ct))
                {
                    count++;
                }

                return handler is null
                    ? new TranscriptionResult { Text = count.ToString() }
                    : await handler(count);
            }
            finally { lock (_lock) { _concurrent--; } }
        }
    }

    [Fact]
    public async Task TranscribeAsync_NoPause_DecodesWholeUtteranceOnce()
    {
        var inner = new FakeStt();

        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6)), new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(1);
        result.Text.ShouldBe("6"); // single segment of 6 chunks
    }

    [Fact]
    public async Task TranscribeAsync_PausesBetweenPhrases_ConcatenatesInOrder()
    {
        var inner = new FakeStt();

        // seg0 = 6 loud + 3 silent = 9 ; seg1 = 7 loud + 3 silent = 10 ; tail seg2 = 8 loud = 8
        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6), Silence(3), Speech(7), Silence(3), Speech(8)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(3);
        result.Text.ShouldBe("9 10 8");
    }

    [Fact]
    public async Task TranscribeAsync_ManySegments_RespectsMaxInFlightDecodes()
    {
        // Each decode holds a slot for 50 ms so overlaps are observable.
        var inner = new FakeStt(async count =>
        {
            await Task.Delay(50);
            return new TranscriptionResult { Text = count.ToString() };
        });

        await New(inner, Config(maxInFlight: 1)).TranscribeAsync(
            Stream(Speech(6), Silence(3), Speech(7), Silence(3), Speech(8)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task TranscribeAsync_ShortFinalPhrase_MergesBackwardIntoPreviousSegment()
    {
        var inner = new FakeStt();

        // seg0 = 6 loud + 3 silent = 9 ; tail = 2 loud (200 ms < 500 ms min) -> merge into seg0 => 11
        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6), Silence(3), Speech(2)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("11"); // single merged segment, not "9 2"
    }
}