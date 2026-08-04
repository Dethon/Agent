using System.Text;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;

namespace Tests.Unit.McpChannelVoice;

// The synthetic audio every playback test drives the queue with: a labelled chunk so a test can read
// back what the satellite would have heard, and a source that fails the way a synthesis failure does.
public static class PlaybackFakes
{
    public static PlaybackJob Job(string label, PlaybackKind kind) => new(
        Label: label,
        Kind: kind,
        Priority: AnnouncePriority.Normal,
        Audio: Audio(label));

    public static AudioChunk Chunk(string label = "x") => new()
    {
        Data = Encoding.UTF8.GetBytes(label),
        Format = AudioFormat.WyomingStandard
    };

    public static async IAsyncEnumerable<AudioChunk> Audio(string label = "x", int count = 1)
    {
        foreach (var _ in Enumerable.Range(0, count))
        {
            yield return Chunk(label);
            await Task.Yield();
        }
    }

    // Throws before yielding anything, which is what a failed synthesis looks like to the queue.
    public static async IAsyncEnumerable<AudioChunk> ThrowingAudio()
    {
        await Task.Yield();
        throw new InvalidOperationException("synthesis failed");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}