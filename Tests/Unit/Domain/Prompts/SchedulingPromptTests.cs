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
}