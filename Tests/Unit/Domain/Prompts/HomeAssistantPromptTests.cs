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

    [Fact]
    public void SystemPrompt_TeachesMusicPlaybackAndGroupingIdiom()
    {
        var prompt = HomeAssistantPrompt.SystemPrompt;

        prompt.ShouldContain("Music playback");
        prompt.ShouldContain("music_assistant.play_media");
        prompt.ShouldContain("media_player.join");
        prompt.ShouldContain("media_player.unjoin");
        prompt.ShouldContain("speaking room"); // default target is the room the request came from
    }
}