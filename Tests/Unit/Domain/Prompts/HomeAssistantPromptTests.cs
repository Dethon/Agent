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
    public void SystemPrompt_TeachesLibraryFirstPlaylistResolution()
    {
        var prompt = HomeAssistantPrompt.SystemPrompt;

        prompt.ShouldContain("app_id");           // MA-player marker attribute
        prompt.ShouldContain("mass_player_type"); // MA-player marker attribute
        prompt.ShouldContain("browse_media.sh --media_content_id playlists"); // list saved playlists first
        prompt.ShouldContain("exact title");      // play what browse returned, never a guess
        prompt.ShouldContain("search_media.sh");  // named as the GLOBAL catalog search
        prompt.ShouldContain("500");              // 500 = unresolved item, not MA down
    }

    // The never-guess rule used to be phrased around how the user worded the request, which left the
    // model to judge whether "música favorita" counted as "my playlist" — it decided it was a free-text
    // name, invented "Mi música favorita", and ate a bare HA 500. Anchor the rule to the flag instead:
    // passing --media_type playlist is itself the trigger to browse first.
    [Fact]
    public void SystemPrompt_AnchorsPlaylistNeverGuessRuleToTheMediaTypeFlag()
    {
        var prompt = HomeAssistantPrompt.SystemPrompt;

        prompt.ShouldContain("--media_type playlist");        // the flag that triggers the rule
        prompt.ShouldContain("in this same turn");            // the title must come from a listing just read
        prompt.ShouldContain("almost never its stored title"); // how the user described it != the title
        prompt.ShouldContain("Mi música favorita");           // the concrete anti-example that failed
    }

    [Fact]
    public void Prompt_TeachesSnoozeAfterDismissal()
    {
        HomeAssistantPrompt.SystemPrompt.ShouldContain("just dismissed");
        HomeAssistantPrompt.SystemPrompt.ShouldContain("new one-shot");
    }

    [Fact]
    public void Prompt_DrawsTheBoundaryAgainstTimersAndSchedules()
    {
        HomeAssistantPrompt.SystemPrompt.ShouldContain("/timers");
        HomeAssistantPrompt.SystemPrompt.ShouldContain("agent tasks");
        HomeAssistantPrompt.SystemPrompt.ShouldContain("duration from now"); // those belong in /timers
    }

    [Fact]
    public void Prompt_RoutesDeferredHomeActionsToSchedules()
    {
        var prompt = HomeAssistantPrompt.SystemPrompt;

        // The calendar and /timers both exist to TELL a person something. "apaga el aire en una hora"
        // asks for something to HAPPEN, so it is a /schedules one-shot despite being a duration.
        prompt.ShouldContain("apaga el aire en una hora");
        prompt.ShouldContain("/schedules` one-shot");
    }
}