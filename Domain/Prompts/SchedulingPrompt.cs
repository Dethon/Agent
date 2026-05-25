namespace Domain.Prompts;

public static class SchedulingPrompt
{
    public const string Name = "scheduling_prompt";

    public const string Description =
        "Explains how to schedule agent tasks via the /schedules filesystem (cron/one-shot, delivery, run-now)";

    public const string Prompt = """
        ## Scheduled Tasks

        You can schedule prompts to run later — once at a future time, or repeatedly on a cron schedule. Schedules live in the virtual filesystem mounted at `/schedules`, one directory per agent, and you manage them entirely with the `domain__filesystem__*` tools. When a schedule fires, its prompt is delivered to an agent as if a user had sent it.

        ### Layout

        - `/schedules` — the root. Each immediate child directory is an **agent** you can schedule work for.
        - `/schedules/<agentId>/agent_info.json` — read this to learn what an agent does before scheduling against it.
        - `/schedules/<agentId>/<scheduleId>/schedule.json` — one schedule. `<scheduleId>` is a descriptive, unique id you choose (e.g. `morning-news`).
        - `/schedules/<agentId>/<scheduleId>/status.json` — read-only timing: `createdAt`, `lastRunAt`, `nextRunAt`.

        ### Creating a schedule

        `fs_create` a `schedule.json` whose content is a JSON object:

        - `prompt` (required) — the instruction delivered to the agent when the schedule fires.
        - `cron` **or** `runAt` — exactly one is required, and they are mutually exclusive.
          - `cron` — a standard 5-field cron expression for a **recurring** schedule. All times are UTC. Examples:
            - `"0 9 * * *"` — every day at 09:00
            - `"0 */2 * * *"` — every 2 hours
            - `"30 14 * * 1-5"` — weekdays at 14:30
          - `runAt` — an ISO-8601 datetime for a **one-shot** schedule. It **must include a time zone**: either `Z` for UTC (e.g. `2026-06-01T14:30:00Z`) or an explicit offset (e.g. `2026-06-01T16:30:00+02:00`); it is normalized to UTC. A datetime without a zone is rejected. It is deleted automatically once it fires.
        - `userId` (optional) — the user the fired prompt should be attributed to.
        - `deliverTo` (optional) — a list of channel ids that should receive the result (e.g. `["signalr", "telegram"]`). Omit to use the configured default.

        A recurring schedule — every day at 09:00 UTC:

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
          "prompt": "Remind me to submit the quarterly report",
          "runAt": "2026-06-01T14:30:00Z"
        }
        ```

        ### Managing schedules

        - **Discover** — `fs_glob` `/schedules` to list agents, then glob `/schedules/<agentId>` to list their schedules.
        - **Change** — `fs_edit` the `schedule.json` to adjust the prompt, timing, or delivery.
        - **Reassign / rename** — `fs_move` a schedule directory to a different `<agentId>` or `<scheduleId>`.
        - **Remove** — `fs_delete` the schedule directory.
        - **Run now** — `fs_exec` `run_now.sh` on a schedule directory to fire it immediately without waiting for its next scheduled time.
        """;
}