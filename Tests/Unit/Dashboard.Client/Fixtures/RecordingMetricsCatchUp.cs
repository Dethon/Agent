using Dashboard.Client.Contracts;

namespace Tests.Unit.Dashboard.Client.Fixtures;

// Records that catch-up ran, and can hold it open or fail it, over the real thing. The counter is
// what proves catch-up did not run on the first connection without going looking through requests.
public sealed class RecordingMetricsCatchUp(IMetricsCatchUp inner) : IMetricsCatchUp
{
    public int Runs { get; private set; }

    public TaskCompletionSource? Gate { get; set; }

    // Holds catch-up open after the real reload has written the stores, which is the window where a
    // push already contained in the snapshot can arrive.
    public TaskCompletionSource? GateAfter { get; set; }

    public Exception? Failure { get; set; }

    public async Task CatchUpAsync()
    {
        Runs++;

        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        try
        {
            await inner.CatchUpAsync();
        }
        finally
        {
            // Held open even when a family's reload failed: what matters is that the stores the
            // reload did write are in place while the gate is held.
            if (GateAfter is { } after)
            {
                await after.Task;
            }
        }
    }
}