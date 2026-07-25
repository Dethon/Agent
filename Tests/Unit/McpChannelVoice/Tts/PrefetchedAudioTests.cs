using System.Runtime.CompilerServices;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class PrefetchedAudioTests
{
    private static AudioChunk Chunk(byte value) =>
        new() { Data = new[] { value }, Format = AudioFormat.WyomingStandard };

    private static async IAsyncEnumerable<AudioChunk> Source(
        TaskCompletionSource pulled, int count, [EnumeratorCancellation] CancellationToken ct = default)
    {
        pulled.TrySetResult();
        foreach (var i in Enumerable.Range(0, count))
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return Chunk((byte)i);
        }
    }

    [Fact]
    public async Task Chunks_AreProducedBeforeAnyoneEnumerates()
    {
        // The whole point: the TTS request goes out while the PREVIOUS segment is still playing, so
        // the playback loop finds the audio waiting instead of paying a round trip at every seam.
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var prefetch = new PrefetchedAudio(Source(pulled, 3), capacity: 8);

        await pulled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Chunks_ReplaysEverythingInOrder()
    {
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var prefetch = new PrefetchedAudio(Source(pulled, 4), capacity: 8);

        var seen = new List<byte>();
        await foreach (var chunk in prefetch.Chunks)
        {
            seen.Add(chunk.Data.Span[0]);
        }

        seen.ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public async Task Chunks_SurfaceTheSourceFailure()
    {
        // A Wyoming/Kokoro error event throws mid-synthesis; the playback loop relies on that
        // surfacing so OnFailed settles the turn instead of hanging the handshake.
        static async IAsyncEnumerable<AudioChunk> Throwing()
        {
            await Task.Yield();
            throw new InvalidOperationException("kokoro exploded");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        await using var prefetch = new PrefetchedAudio(Throwing(), capacity: 8);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in prefetch.Chunks)
            { }
        });
    }

    [Fact]
    public async Task DisposeAsync_StopsThePumpWhenNobodyConsumes()
    {
        // A segment the playback queue refused is never enumerated, so disposal is the only thing
        // that releases the in-flight HTTP response.
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> Endless([EnumeratorCancellation] CancellationToken ct = default)
        {
            pulled.TrySetResult();
            try
            {
                while (true)
                {
                    await Task.Delay(10, ct);
                    yield return Chunk(1);
                }
            }
            finally
            {
                cancelled.TrySetResult();
            }
        }

        var prefetch = new PrefetchedAudio(Endless(), capacity: 4);
        await pulled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await prefetch.DisposeAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Capacity_BoundsHowFarAheadItRuns()
    {
        // Unbounded would let a long announcement buffer its whole synthesis into memory; the
        // producer must park once the buffer is full and resume as the loop drains it.
        var produced = 0;
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> Counting([EnumeratorCancellation] CancellationToken ct = default)
        {
            pulled.TrySetResult();
            foreach (var i in Enumerable.Range(0, 100))
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref produced);
                await Task.Yield();
                yield return Chunk((byte)i);
            }
        }

        await using var prefetch = new PrefetchedAudio(Counting(), capacity: 4);
        await pulled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        // capacity + the one the producer is parked on, with generous slack for scheduling
        Volatile.Read(ref produced).ShouldBeLessThan(20);
    }
}