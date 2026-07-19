using Domain.Contracts;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// Polls the timer store once per second and rings each due timer as an insistent timer alert —
// no HTTP hop, no LLM in the fire path. Timers use a fixed tighter cadence than alarms and skip
// the volume ramp (a kitchen countdown must be heard on round 1; the ramp is for wake-up alarms).
public sealed class TimerFireService(
    ITimerStore store,
    IInsistentAnnouncer announcer,
    TimeProvider time,
    ILogger<TimerFireService> logger) : BackgroundService
{
    private static readonly InsistentOptions _timerRing = new()
    {
        GapSeconds = 10, MaxRepeats = 12, RampStartPercent = 100
    };

    // Constructed eagerly (not inside ExecuteAsync) so the first tick's due-time is pinned relative to
    // `time` at service-construction, not whenever the base BackgroundService.StartAsync's Task.Run
    // happens to schedule ExecuteAsync onto a pool thread. PeriodicTimer(period, TimeProvider) registers
    // its ITimer with the provider in its own constructor, so a lazily-constructed timer under a
    // FakeTimeProvider can miss an Advance() that races ahead of that scheduling delay.
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1), time);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct))
            {
                var due = await store.TakeDueAsync(time.GetUtcNow().UtcDateTime, ct);
                foreach (var armed in due)
                {
                    await FireAsync(armed, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }

    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    private async Task FireAsync(ArmedTimer armed, CancellationToken ct)
    {
        try
        {
            await announcer.StartAsync(new AnnounceRequest
            {
                Target = armed.Target,
                Text = armed.Text ?? $"{armed.Id} timer",
                Kind = AnnounceKind.Timer,
                Insistent = _timerRing
            }, ct);
            logger.LogInformation("Timer {TimerId} fired", armed.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The timer was already removed by TakeDueAsync; a bad target means it just doesn't ring
            // (documented v1 behavior — no durability/retry).
            logger.LogWarning(ex, "Timer {TimerId} failed to ring", armed.Id);
        }
    }
}