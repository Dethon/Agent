using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public readonly record struct InsistentPlan(
    TimeSpan Gap, int MaxRepeats, TimeSpan? MaxDuration, double RampStart, int RampRounds)
{
    public static InsistentPlan Resolve(InsistentOptions? options, InsistentDefaults defaults)
    {
        var gap = TimeSpan.FromSeconds(options?.GapSeconds ?? defaults.GapSeconds);

        // A request that sets only a duration is duration-bounded (unbounded repeats); otherwise the
        // repeat count applies. With both set, the loop stops at whichever is reached first.
        var maxRepeats = options?.MaxRepeats
            ?? (options?.MaxDurationSeconds is > 0 ? int.MaxValue : defaults.MaxRepeats);

        var maxDurationSeconds = options?.MaxDurationSeconds ?? defaults.MaxDurationSeconds;
        var maxDuration = maxDurationSeconds is > 0
            ? TimeSpan.FromSeconds(maxDurationSeconds.Value)
            : (TimeSpan?)null;

        var rampStart = Math.Clamp(defaults.RampStartPercent, 1, 100) / 100.0;
        return new InsistentPlan(gap, maxRepeats, maxDuration, rampStart, Math.Max(1, defaults.RampRounds));
    }

    // Playback gain for a 0-based round: linear from RampStart to 1.0 across the first RampRounds
    // rounds, full volume after.
    public double GainFor(int round) =>
        RampStart >= 1.0 || RampRounds <= 1
            ? 1.0
            : Math.Min(1.0, RampStart + (1.0 - RampStart) * round / (RampRounds - 1));
}