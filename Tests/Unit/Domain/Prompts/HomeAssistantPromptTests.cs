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
        prompt.ShouldContain("music_assistant.play_media.sh --media_id"); // play by NAME via the MA action
        prompt.ShouldContain("media_player.play_media");                  // names the bare action it warns against
        prompt.ShouldContain("`join.sh`");                                // grouping (backtick-bounded, distinct from unjoin)
        prompt.ShouldContain("`unjoin.sh`");                              // ungrouping
        prompt.ShouldContain("speaking room"); // default target is the room the request came from
    }

    [Fact]
    public void Prompt_TeachesSnoozeAfterDismissal()
    {
        HomeAssistantPrompt.SystemPrompt.ShouldContain("just dismissed");
        HomeAssistantPrompt.SystemPrompt.ShouldContain("new one-shot");
    }
}