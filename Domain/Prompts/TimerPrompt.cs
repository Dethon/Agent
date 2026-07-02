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

        - Create: `{{VfsTextCreateTool.Name}}` at `/timers/<descriptive-id>/timer.json` with JSON
          `{"durationSeconds": <int>, "text"?: "<spoken message>", "target": {...} }`.
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
        """;
}