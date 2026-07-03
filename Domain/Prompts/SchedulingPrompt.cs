namespace Domain.Prompts;

public static class SchedulingPrompt
{
    public const string Name = "scheduling_prompt";

    public const string Description =
        "Explains how to schedule agent tasks via the /schedules filesystem (cron/one-shot, delivery, run-now)";

    public static string Build(string zoneId) =>
        $$"""
        ## Scheduled Tasks

        You can schedule prompts to run later — once at a future time, or repeatedly on a cron schedule. Schedules live in the virtual filesystem mounted at `/schedules`, one directory per agent, and you manage them entirely with the `domain__filesystem__*` tools. When a schedule fires, its prompt is delivered to an agent as if a user had sent it.

        `/schedules` is **not an alarm clock**: human alarms, wake-ups, and reminders belong on the HA alarms calendar (clock times, recurring) or in `/timers` (durations from now, e.g. "in 20 minutes") — both ring insistently until acknowledged. A schedule's voice delivery speaks once at most and skips offline satellites — never use it to remind a person of something.

        ### Layout

        - `/schedules` — the root. Each immediate child directory is an **agent** you can schedule work for.
        - `/schedules/<agentId>/agent_info.json` — read this to learn what an agent does before scheduling against it.
        - `/schedules/<agentId>/<scheduleId>/schedule.json` — one schedule. `<scheduleId>` is a descriptive, unique id you choose (e.g. `morning-news`).
        - `/schedules/<agentId>/<scheduleId>/status.json` — read-only timing: `createdAt`, `lastRunAt`, `nextRunAt`, shown in the **{{zoneId}}** time zone.

        ### Creating a schedule

        `text_create` a `schedule.json` whose content is a JSON object:

        - `prompt` (required) — the instruction delivered to the agent when the schedule fires.
        - `cron` **or** `runAt` — exactly one is required, and they are mutually exclusive.
          - `cron` — a standard 5-field cron expression for a **recurring** schedule. Times are interpreted in the **{{zoneId}}** time zone and adjust automatically across daylight-saving changes. Examples:
            - `"0 9 * * *"` — every day at 09:00 {{zoneId}} time
            - `"0 */2 * * *"` — every 2 hours
            - `"30 14 * * 1-5"` — weekdays at 14:30 {{zoneId}} time
          - `runAt` — an ISO-8601 datetime for a **one-shot** schedule. You may include a time zone — `Z` for UTC (e.g. `2026-06-01T14:30:00Z`) or an explicit offset (e.g. `2026-06-01T16:30:00+02:00`) — or omit it, in which case it is read as **{{zoneId}}** local time (e.g. `2026-06-01T18:00:00`). It is stored as UTC and deleted automatically once it fires.
        - `userId` (optional) — the user the fired prompt should be attributed to.
        - `deliverTo` (optional) — a list of channel ids that should receive the result (e.g. `["signalr", "telegram"]`). Omit to use the configured default.

          **Voice delivery (speak the result aloud).** A `deliverTo` entry may target the voice channel:
          - `"voice"` or `"voice:all"` — speak on every voice satellite.
          - `"voice:<satelliteId>"` — speak on one specific satellite (e.g. `"voice:office-01"`).
          - Repeat `"voice:<satelliteId>"` for several specific satellites — each is spoken once, e.g. `["signalr", "voice:office-01", "voice:kitchen-01"]`.

          Add a voice target **only when the user explicitly asked to be notified by voice** (spoken aloud / announced). Otherwise omit voice — **silence is the default**. For example, a schedule that starts the air conditioning at night must NOT announce. Offline satellites are skipped silently. To keep tool-approval prompts answerable, list a non-voice channel first, e.g. `["signalr", "voice:fran-office-01"]`.

        A recurring schedule — every day at 09:00 {{zoneId}} time:

        ```json
        {
          "prompt": "Summarize today's tech news and send me the highlights",
          "cron": "0 9 * * *",
          "deliverTo": ["signalr"]
        }
        ```

        A one-shot schedule — fires once, then deletes itself:

        ```json
        {
          "prompt": "Check whether the media library import finished and report the result",
          "runAt": "2026-06-01T14:30:00"
        }
        ```

        ### Managing schedules

        - **Discover** — `glob` `/schedules` to list agents, then glob `/schedules/<agentId>` to list their schedules.
        - **Change** — `text_edit` the `schedule.json` to adjust the prompt, timing, or delivery.
        - **Reassign / rename** — `move` a schedule directory to a different `<agentId>` or `<scheduleId>`.
        - **Remove** — `remove` the schedule directory.
        - **Run now** — `exec` `run_now.sh` on a schedule directory to fire it immediately without waiting for its next scheduled time.
        """;
}