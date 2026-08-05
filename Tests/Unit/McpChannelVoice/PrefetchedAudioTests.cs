using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using Shouldly;
using static Tests.Unit.McpChannelVoice.PlaybackFakes;

namespace Tests.Unit.McpChannelVoice;

public class PrefetchedAudioTests
{
    [Fact]
    public async Task DisposeAsync_CalledTwice_TheSecondCallIsANoOp()
    {
        // Two owners legitimately release the same in-flight job on shutdown-cancel: the loop's
        // finally and the drain's DiscardUnplayed sweep. The second release used to cancel a
        // disposed CancellationTokenSource and fault the discarded task with
        // ObjectDisposedException, so disposal latches on the first call.
        var prefetch = new PrefetchedAudio(Audio(), capacity: 1);
        await prefetch.DisposeAsync();

        await Should.NotThrowAsync(async () => await prefetch.DisposeAsync());
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentWithARunningPump_EveryCallCompletesCleanly()
    {
        var releaseSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefetch = new PrefetchedAudio(ParkedSynthesis(releaseSource), capacity: 1);

        await Should.NotThrowAsync(async () =>
            await Task.WhenAll(
                prefetch.DisposeAsync().AsTask(),
                prefetch.DisposeAsync().AsTask()));
        await releaseSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async IAsyncEnumerable<AudioChunk> ParkedSynthesis(TaskCompletionSource released)
    {
        try
        {
            while (true)
            {
                yield return Chunk();
                await Task.Delay(10);
            }
        }
        finally
        {
            released.TrySetResult();
        }
    }
}