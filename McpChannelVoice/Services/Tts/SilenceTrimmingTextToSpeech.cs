using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services.Tts;

// Drops the trailing run of (near-)silence Piper appends to each utterance. That silence is
// otherwise streamed to the satellite AND waited out by the playback loop (it counts toward the
// nominal audio duration) before the turn completes, padding the gap before the follow-up beep.
// Leading and inter-word silence are preserved; only audio after the last sample at or above the
// threshold is dropped. Only 16-bit mono PCM is trimmed; any other format passes through untouched.
public sealed class SilenceTrimmingTextToSpeech(ITextToSpeech inner, int threshold) : ITextToSpeech
{
    public static ITextToSpeech Wrap(ITextToSpeech inner, int threshold) =>
        threshold > 0 ? new SilenceTrimmingTextToSpeech(inner, threshold) : inner;

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text, SynthesisOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        // Once speech has been seen, silence is held back: inter-word silence is released when the
        // next speech arrives, while a run of silence that reaches end-of-stream is the trailing
        // pad and is dropped. Before any speech, chunks pass straight through so leading silence
        // never delays first audio.
        var seenSpeech = false;
        var pending = new List<AudioChunk>();

        await foreach (var chunk in inner.SynthesizeAsync(text, options, ct))
        {
            var hasSpeech = TryFindSpeechEnd(chunk, threshold, out var end);

            if (!seenSpeech)
            {
                if (!hasSpeech)
                {
                    yield return chunk;
                    continue;
                }
                seenSpeech = true;
            }
            else if (!hasSpeech)
            {
                pending.Add(chunk);
                continue;
            }

            foreach (var held in pending)
            {
                yield return held;
            }
            pending.Clear();

            if (end >= chunk.Data.Length)
            {
                yield return chunk;
            }
            else
            {
                yield return chunk with { Data = chunk.Data[..end] };
                pending.Add(chunk with { Data = chunk.Data[end..] });
            }
        }
        // pending holds only trailing silence here — dropped by falling off the end.
    }

    // Byte offset just past the last sample at or above the threshold; false if the whole chunk is
    // silence. Non-(16-bit mono) formats are reported as all-speech so they pass through untrimmed.
    private static bool TryFindSpeechEnd(AudioChunk chunk, int threshold, out int end)
    {
        end = chunk.Data.Length;
        if (chunk.Format.SampleWidthBytes != 2 || chunk.Format.Channels != 1)
        {
            return true;
        }

        var span = chunk.Data.Span;
        for (var i = span.Length - 2; i >= 0; i -= 2)
        {
            var sample = (short)(span[i] | (span[i + 1] << 8));
            if (Math.Abs((int)sample) >= threshold)
            {
                end = i + 2;
                return true;
            }
        }
        return false;
    }
}