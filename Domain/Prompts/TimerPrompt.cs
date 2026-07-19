using Domain.Tools.FileSystem;

namespace Domain.Prompts;

public static class TimerPrompt
{
    public const string Name = "timers_prompt";
    public const string Description =
        "Explains how to manage short countdown timers via the /timers filesystem";

    public static readonly string Prompt = $$"""
        ## Timers

        Short countdowns ("set a timer for 5 minutes", "pasta timer for 8 minutes") live in the
        virtual filesystem at `/timers` — NOT the Home Assistant alarms calendar (that is for
        clock-time alarms and reminders) and NOT `/schedules` (agent tasks). When a timer expires
        it rings insistently (tone + spoken message) on the target satellites until the user says
        the wake word there, presses the button, or a repeat cap is reached.

        Choosing the mechanism — go by HOW the time is expressed, not the wording: a duration
        from now up to 4 hours ("timer for 10 minutes", "avísame en 5 minutos", "remind me in
        20 minutes") is a `/timers` countdown — put the message to speak in `text`. A clock time
        or date ("wake me at 7", "tomorrow at 9:30"), anything recurring, or anything past the
        4-hour ceiling goes on the HA alarms calendar: it survives restarts and can escalate to
        the phone. `/schedules` is only for agent tasks, never for human alarms or reminders.

        - Create: `{{VfsTextCreateTool.Name}}` at `/timers/<descriptive-id>/timer.json` with JSON
          `{"durationSeconds": <int>, "text"?: "<spoken message>", "target": {...} }`.
          `durationSeconds` is capped at 4 hours — for anything longer use the alarms calendar.
          `target` is `{satelliteId | satelliteIds | room | all}` — default to the **speaking room**
          (the room this request came from) unless another room is named. When `text` is omitted
          the timer announces itself as "<id> timer", so pick a descriptive id (e.g. `pasta`).
        - Time left: `{{VfsTextReadTool.Name}}` on `/timers/<id>/status.json` → `remainingSeconds`
          and `firesAt`.
        - List: `{{VfsGlobFilesTool.Name}}` on `/timers`.
        - Cancel: `{{VfsRemoveTool.Name}}` on `/timers/<id>`.
        - Timers are immutable and fire once — to change one, delete it and create a new one. To
          extend a timer the user just dismissed ("two more minutes"), create a new timer with the
          remaining request.
        - To change a **running** timer ("add five minutes to the pasta timer"): read its
          `status.json` for `remainingSeconds`, delete the timer, and recreate it with the
          adjusted remainder.
        - Stop ringing: when the user asks to stop or dismiss a ringing alarm/timer (from any room
          or any channel), `{{VfsExecTool.Name}}` `dismiss.sh` at `/timers` — it silences everything
          currently ringing on all satellites and reports what was dismissed. A fired timer no
          longer appears under `/timers`; `dismiss.sh` is the only way to silence it remotely.
        """;
}