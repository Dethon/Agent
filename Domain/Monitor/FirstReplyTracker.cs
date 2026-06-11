using System.Diagnostics;

namespace Domain.Monitor;

internal sealed class FirstReplyTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _fired;

    public long? TryComplete() =>
        Interlocked.Exchange(ref _fired, 1) == 0 ? _stopwatch.ElapsedMilliseconds : null;
}