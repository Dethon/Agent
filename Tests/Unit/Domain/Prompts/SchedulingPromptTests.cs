using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class SchedulingPromptTests
{
    [Fact]
    public void Prompt_DrawsTheBoundaryAgainstHumanAlarmsAndTimers()
    {
        var prompt = SchedulingPrompt.Build("Europe/Madrid");

        prompt.ShouldContain("not an alarm clock");
        prompt.ShouldContain("alarms calendar");
        prompt.ShouldContain("/timers");
    }

    [Fact]
    public void Prompt_OneShotExample_ModelsAnAgentTaskNotAHumanReminder()
    {
        SchedulingPrompt.Build("Europe/Madrid").ShouldNotContain("Remind me");
    }

    [Fact]
    public void Prompt_ClaimsDeferredActionsEvenWhenPhrasedAsADuration()
    {
        var prompt = SchedulingPrompt.Build("Europe/Madrid");

        // The boundary paragraph only pushed work AWAY from /schedules, so a duration ("in an hour")
        // pulled device actions into /timers — where a timer only speaks and nothing gets switched off.
        prompt.ShouldContain("deferred action");
        prompt.ShouldContain("does not make it a timer");
        prompt.ShouldContain("runAt");
    }
}