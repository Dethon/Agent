using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

// Decodes an utterance as it streams: an internal SilenceGate tuned for phrase
// pauses slices the audio at natural silences, each closed phrase is decoded in
// the background via the inner backend, and the segment transcripts are
// concatenated in order. Segments are disjoint in time, so total decode work is
// ~one whole-utterance decode, just overlapped with speech. The single final
// TranscriptionResult is indistinguishable to callers from a batch transcript.
public sealed class SegmentedSpeechToText(
    ISpeechToText inner,
    SegmentedSttConfig config,
    ILogger<SegmentedSpeechToText> logger) : ISpeechToText
{
    private sealed record Segment(IReadOnlyList<AudioChunk> Audio, Task<TranscriptionResult> Task);

    public static ISpeechToText Wrap(ISpeechToText inner, SegmentedSttConfig config, ILoggerFactory loggers) =>
        config.Enabled
            ? new SegmentedSpeechToText(inner, config, loggers.CreateLogger<SegmentedSpeechToText>())
            : inner;

    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
    {
        var minSpeech = TimeSpan.FromMilliseconds(config.MinSegmentMs);
        var gate = new SilenceGate(
            config.SilenceRmsThreshold,
            TimeSpan.FromMilliseconds(config.SegmentSilenceMs),
            TimeSpan.MaxValue,
            minSpeech);
        // Not disposed deliberately: a merge-backward re-decode can supersede an
        // in-flight segment decode that is no longer awaited, and that detached task
        // must still Release() safely after this method returns. SemaphoreSlim needs
        // no disposal unless AvailableWaitHandle is used (it isn't).
        var slot = new SemaphoreSlim(Math.Max(1, config.MaxInFlightDecodes));
        var segments = new List<Segment>();
        var all = new List<AudioChunk>();
        var current = new List<AudioChunk>();

        try
        {
            await foreach (var chunk in audio.WithCancellation(ct))
            {
                all.Add(chunk);
                current.Add(chunk);
                if (gate.Process(chunk.Data.Span, chunk.Format.SampleRateHz,
                        chunk.Format.SampleWidthBytes, chunk.Format.Channels) == SilenceGate.Decision.EndUtterance)
                {
                    var closed = current;
                    current = new List<AudioChunk>();
                    gate.Reset();
                    segments.Add(new Segment(closed, StartDecode(closed, options, slot, ct)));
                }
            }
        }
        catch
        {
            // Audio enumeration aborted (e.g. cancellation): observe the decode tasks already
            // started so a later non-OCE fault doesn't surface as an unobserved TaskException.
            foreach (var seg in segments)
            {
                ObserveAndDiscard(seg.Task);
            }
            throw;
        }

        if (gate.SpeechElapsed > TimeSpan.Zero)
        {
            if (gate.SpeechElapsed >= minSpeech || segments.Count == 0)
            {
                segments.Add(new Segment(current, StartDecode(current, options, slot, ct)));
            }
            else
            {
                var prev = segments[^1];
                ObserveAndDiscard(prev.Task);
                var merged = prev.Audio.Concat(current).ToList();
                segments[^1] = new Segment(merged, StartDecode(merged, options, slot, ct));
            }
        }

        if (segments.Count == 0)
        {
            return new TranscriptionResult { Text = "" };
        }

        try
        {
            var results = new List<TranscriptionResult>(segments.Count);
            foreach (var seg in segments)
            {
                results.Add(await seg.Task);
            }

            var weighted = segments
                .Select((seg, i) => (Weight: Math.Max(DurationSeconds(seg.Audio), 1e-9), Result: results[i]))
                .ToList();

            logger.LogInformation("Segmented STT finalized {Segments} segment(s)", segments.Count);
            return new TranscriptionResult
            {
                Text = string.Join(" ", results
                    .Select(r => r.Text?.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))),
                Language = results.Select(r => r.Language).FirstOrDefault(l => l is not null),
                Confidence = WeightedMean(weighted, r => r.Confidence),
                AvgLogProb = WeightedMean(weighted, r => r.AvgLogProb),
                NoSpeechProb = WeightedMean(weighted, r => r.NoSpeechProb),
                CompressionRatio = results.Max(r => r.CompressionRatio)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Segmented decode failed; falling back to whole-utterance decode");
            foreach (var seg in segments)
            {
                ObserveAndDiscard(seg.Task);
            }
            return await inner.TranscribeAsync(ToAsyncEnumerable(all), options, ct);
        }
    }

    private Task<TranscriptionResult> StartDecode(
        IReadOnlyList<AudioChunk> chunks, TranscriptionOptions options, SemaphoreSlim slot, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await slot.WaitAsync(ct);
                acquired = true;
                return await inner.TranscribeAsync(ToAsyncEnumerable(chunks), options, ct);
            }
            finally
            {
                if (acquired)
                {
                    slot.Release();
                }
            }
        }, ct);

    // Segments differ in length, so a plain mean would let a half-second noise segment outvote
    // ten seconds of clean speech (and vice versa). Weight by audio duration; segments that
    // report no value abstain rather than dragging the average (fail-open composition).
    private static double? WeightedMean(
        IReadOnlyList<(double Weight, TranscriptionResult Result)> weighted,
        Func<TranscriptionResult, double?> selector)
    {
        var pairs = weighted
            .Where(w => selector(w.Result) is not null)
            .Select(w => (w.Weight, Value: selector(w.Result)!.Value))
            .ToList();
        return pairs.Count > 0
            ? pairs.Sum(p => p.Weight * p.Value) / pairs.Sum(p => p.Weight)
            : null;
    }

    private static double DurationSeconds(IReadOnlyList<AudioChunk> chunks) =>
        chunks.Sum(c =>
        {
            var bytesPerSecond = c.Format.SampleRateHz * c.Format.SampleWidthBytes * c.Format.Channels;
            return bytesPerSecond == 0 ? 0 : (double)c.Data.Length / bytesPerSecond;
        });

    private static void ObserveAndDiscard(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    private static async IAsyncEnumerable<AudioChunk> ToAsyncEnumerable(IReadOnlyList<AudioChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
        await Task.CompletedTask;
    }
}