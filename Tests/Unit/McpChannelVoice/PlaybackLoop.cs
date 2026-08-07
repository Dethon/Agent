namespace Tests.Unit.McpChannelVoice;

// How a playback loop is stopped, for tests that run one. The queue's own closing verb is the link
// drop; the loop itself ends on the token it was started with, and it reports that cancellation by
// throwing — which is what the connection's run relies on. Waiting for the jobs under test to settle
// before cancelling is the caller's job, exactly as the connection's drain does it.
internal static class PlaybackLoop
{
    private static readonly TimeSpan _stopTimeout = TimeSpan.FromSeconds(5);

    // What a test waits for before it stops the loop: the thing it is about having happened. The
    // loop ending is no longer that signal, because ending it is now the test's own act.
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + _stopTimeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (!condition())
        {
            throw new TimeoutException($"Timed out waiting for {what}");
        }
    }

    public static async Task StopAsync(this CancellationTokenSource run, Task loop)
    {
        await run.CancelAsync();
        try
        {
            await loop.WaitAsync(_stopTimeout);
        }
        catch (OperationCanceledException)
        {
        }
    }
}