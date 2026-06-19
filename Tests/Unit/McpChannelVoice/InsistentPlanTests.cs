using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class InsistentPlanTests
{
    private static readonly InsistentDefaults Defaults = new();

    [Fact]
    public void Resolve_NullOptions_UsesDefaults()
    {
        var plan = InsistentPlan.Resolve(null, Defaults);

        plan.Gap.ShouldBe(TimeSpan.FromSeconds(30));
        plan.MaxRepeats.ShouldBe(5);
        plan.MaxDuration.ShouldBeNull();
    }

    [Fact]
    public void Resolve_RequestOverridesGapAndRepeats()
    {
        var plan = InsistentPlan.Resolve(new InsistentOptions { GapSeconds = 10, MaxRepeats = 3 }, Defaults);

        plan.Gap.ShouldBe(TimeSpan.FromSeconds(10));
        plan.MaxRepeats.ShouldBe(3);
    }

    [Fact]
    public void Resolve_DurationOnly_IsDurationBoundedNotClippedToDefaultRepeats()
    {
        // A request that sets only MaxDurationSeconds must be bounded by duration, not also
        // silently capped at the default repeat count.
        var plan = InsistentPlan.Resolve(new InsistentOptions { MaxDurationSeconds = 120 }, Defaults);

        plan.MaxRepeats.ShouldBe(int.MaxValue);
        plan.MaxDuration.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Resolve_NonPositiveDuration_IsTreatedAsNoDurationCap()
    {
        var plan = InsistentPlan.Resolve(new InsistentOptions { MaxDurationSeconds = 0 }, Defaults);

        plan.MaxDuration.ShouldBeNull();
        plan.MaxRepeats.ShouldBe(5);
    }
}