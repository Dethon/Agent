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
}