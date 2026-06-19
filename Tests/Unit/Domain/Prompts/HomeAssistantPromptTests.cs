using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class HomeAssistantPromptTests
{
    [Fact]
    public void SystemPrompt_TeachesAlarmReminderCalendarIdiom()
    {
        var prompt = HomeAssistantPrompt.SystemPrompt;

        prompt.ShouldContain("Alarms & reminders");
        prompt.ShouldContain("calendar.create_event");
        prompt.ShouldContain("description");  // JSON params carried in the event description
        prompt.ShouldContain("rrule");        // recurrence
        prompt.ShouldContain("insistent` (an object"); // nested insistent object, not a top-level boolean flag
    }
}