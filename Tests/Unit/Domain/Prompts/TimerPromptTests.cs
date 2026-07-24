using Domain.DTOs.Voice;
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
    public void Prompt_TargetDefaultIsChannelConditional()
    {
        // On a voice turn, default to the speaking room; on a text channel there is no speaking
        // room, so the agent must ask which room rather than guess a target the timer would ring
        // in the wrong place (or fail to arm against the create-time target validation).
        TimerPrompt.Prompt.ShouldContain("speaking room");
        TimerPrompt.Prompt.ShouldContain("no speaking room");
        TimerPrompt.Prompt.ShouldContain("never guess");
    }

    [Fact]
    public void Prompt_TimeLeftBrevityIsScopedToSpokenReplies()
    {
        // "Speak only the remaining time" is a spoken-brevity rule; a written channel asking when a
        // timer fires wants firesAt too, so the rule is scoped to spoken replies.
        TimerPrompt.Prompt.ShouldContain("firesAt");
        TimerPrompt.Prompt.ShouldContain("when your reply is spoken");
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

    [Fact]
    public void Build_WithRoster_ListsSatelliteIdsAndRoomsAfterTheIdiomText()
    {
        var result = TimerPrompt.Build(
            [new SatelliteDescriptor("kitchen-01", "Kitchen"), new SatelliteDescriptor("fran-office-01", "Fran's office")]);

        // The roster lets a text-channel agent OFFER the rooms instead of asking blind.
        result.ShouldContain("kitchen-01 — Kitchen");
        result.ShouldContain("fran-office-01 — Fran's office");
        result.ShouldContain(TimerPrompt.Prompt);
    }

    [Fact]
    public void Build_EmptyRoster_IsExactlyTheBasePrompt()
    {
        // Fail-open shape: when the hub cannot be asked at prompt-fetch time the prompt degrades
        // to the static idiom text, which already tells the agent to ask which room.
        TimerPrompt.Build([]).ShouldBe(TimerPrompt.Prompt);
    }
}