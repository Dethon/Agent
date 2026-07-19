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
    public void Prompt_RoutesDurationRequestsToTimers_AndClockTimesToCalendar()
    {
        TimerPrompt.Prompt.ShouldContain("avísame en 5 minutos"); // duration-from-now reminders are timers
        TimerPrompt.Prompt.ShouldContain("clock time");
        TimerPrompt.Prompt.ShouldContain("escalate"); // the calendar's durability rationale stays visible
    }

    [Fact]
    public void Prompt_TeachesExtendingARunningTimer()
    {
        TimerPrompt.Prompt.ShouldContain("adjusted remainder");
    }
}