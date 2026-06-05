using System.Collections.Concurrent;

namespace McpChannelVoice.Services;

public sealed class ApprovalCaptureBroker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    public bool HasListener(string satelliteId) => _pending.ContainsKey(satelliteId);

    public Task<string> WaitForUtteranceAsync(string satelliteId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[satelliteId] = tcs;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var registration = cts.Token.Register(() =>
        {
            if (_pending.TryRemove(satelliteId, out var pending) && pending == tcs)
            {
                tcs.TrySetResult("");
            }
        });

        // Dispose the linked source and its registration once the wait settles (answered or timed
        // out) so the timer and linked-token callback don't leak across approval requests. The tcs
        // completes continuations asynchronously, so this never disposes inside the cancel callback.
        _ = tcs.Task.ContinueWith(
            _ =>
            {
                registration.Dispose();
                cts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        return tcs.Task;
    }

    public bool SubmitUtterance(string satelliteId, string text)
    {
        if (_pending.TryRemove(satelliteId, out var tcs))
        {
            tcs.TrySetResult(text);
            return true;
        }
        return false;
    }
}