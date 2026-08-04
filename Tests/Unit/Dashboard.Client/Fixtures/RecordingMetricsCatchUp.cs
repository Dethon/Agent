using Dashboard.Client.Contracts;

namespace Tests.Unit.Dashboard.Client.Fixtures;

// Records that catch-up ran, and can hold it open or fail it, over the real thing. The counter is
// what proves catch-up did not run on the first connection without going looking through requests.
public sealed class RecordingMetricsCatchUp(IMetricsCatchUp inner) : IMetricsCatchUp
{
    public int Runs { get; private set; }

    public TaskCompletionSource? Gate { get; set; }

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

        await inner.CatchUpAsync();
    }
}