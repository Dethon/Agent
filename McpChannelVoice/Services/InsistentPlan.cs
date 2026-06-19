using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public readonly record struct InsistentPlan(TimeSpan Gap, int MaxRepeats, TimeSpan? MaxDuration)
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

        return new InsistentPlan(gap, maxRepeats, maxDuration);
    }
}