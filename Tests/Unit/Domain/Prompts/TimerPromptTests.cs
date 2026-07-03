using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class TimerPromptTests
{
    [Fact]
    public void Prompt_TeachesTheTimersIdiom()
    {
        TimerPrompt.Prompt.ShouldContain("/timers");
        TimerPrompt.Prompt.ShouldContain("durationSeconds");
        TimerPrompt.Prompt.ShouldContain("status.json");
        TimerPrompt.Prompt.ShouldContain("speaking room");
        TimerPrompt.Prompt.ShouldContain("calendar"); // steers alarms back to the HA calendar
    }

    [Fact]
    public void Prompt_TeachesTheDurationCeiling()
    {
        TimerPrompt.Prompt.ShouldContain("4 hours");
    }

    [Fact]
    public void Prompt_TeachesDismissingRingingAlerts()
    {
        TimerPrompt.Prompt.ShouldContain("dismiss.sh");
    }

    [Fact]
    public void Prompt_SteersRemindersToTheCalendar_NotTimers()
    {
        TimerPrompt.Prompt.ShouldContain("escalate");
        TimerPrompt.Prompt.ShouldContain("reminded");
    }

    [Fact]
    public void Prompt_TeachesExtendingARunningTimer()
    {
        TimerPrompt.Prompt.ShouldContain("adjusted remainder");
    }
}