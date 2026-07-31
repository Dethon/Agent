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

    // The agent that writes a schedule is not necessarily the agent that runs it: ownership is the
    // <agentId> path segment and nothing else re-derives it at fire time. Told only to "read
    // agent_info.json to learn what an agent does", an agent whose own catalog description
    // advertises reply style rather than abilities hands its own deferred actions to whichever
    // agent's blurb names the subject -- and the result then comes back on that agent's channel.
    [Fact]
    public void Prompt_DefaultsOwnershipToTheSchedulingAgentItself()
    {
        var prompt = SchedulingPrompt.Build("Europe/Madrid");

        prompt.ShouldContain("schedule against yourself");
        prompt.ShouldContain("unless the user names another agent");
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