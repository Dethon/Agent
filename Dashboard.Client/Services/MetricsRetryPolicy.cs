using Microsoft.AspNetCore.SignalR.Client;

namespace Dashboard.Client.Services;

// How long before the dashboard gives up reconnecting: never. The first four delays are the
// framework's own defaults, and thirty seconds is the last of them, so this is exactly "keep going"
// rather than a new schedule. The same schedule drives the live connection's first-start loop,
// which automatic reconnection has never covered.
public sealed class MetricsRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] _schedule =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    public static TimeSpan DelayFor(long previousRetryCount) =>
        _schedule[(int)Math.Min(previousRetryCount, _schedule.Length - 1)];

    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        DelayFor(retryContext.PreviousRetryCount);
}