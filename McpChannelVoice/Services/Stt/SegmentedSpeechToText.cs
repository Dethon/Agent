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
        var current = new List<AudioChunk>();

        await foreach (var chunk in audio.WithCancellation(ct))
        {
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

        var results = new List<TranscriptionResult>(segments.Count);
        foreach (var seg in segments)
        {
            results.Add(await seg.Task);
        }

        logger.LogInformation("Segmented STT finalized {Segments} segment(s)", segments.Count);
        return new TranscriptionResult
        {
            Text = string.Join(" ", results
                .Select(r => r.Text?.Trim())
                .Where(t => !string.IsNullOrEmpty(t))),
            Language = results.Select(r => r.Language).FirstOrDefault(l => l is not null),
            Confidence = null
        };
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