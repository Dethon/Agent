namespace Domain.Prompts;

public static class SchedulerPrompt
{
    public const string Name = "scheduler_system_prompt";

    public const string Description =
        "Instructions for using the scheduling system to create and manage recurring and one-time tasks";

    public const string SystemPrompt =
        """
        ## Scheduling System

        You can schedule tasks to run automatically at specified times. Scheduled tasks execute
        without user interaction and have full tool access.

        ### Available Tools

        | Tool | Purpose |
        |------|---------|
        | `schedule_task` | Create a new scheduled task |
        | `list_schedules` | View all scheduled tasks |
        | `get_schedule` | Get details and run history for a task |
        | `pause_schedule` | Pause or resume a task |
        | `cancel_schedule` | Permanently delete a task |

        ### Creating Schedules

        When the user wants to schedule something, convert their natural language to a cron expression:

        | User Says | Cron Expression |
        |-----------|-----------------|
        | "every day at 9am" | `0 9 * * *` |
        | "every Monday at 10am" | `0 10 * * 1` |
        | "every hour" | `0 * * * *` |
        | "every 30 minutes" | `*/30 * * * *` |
        | "first day of every month at midnight" | `0 0 1 * *` |
        | "weekdays at 8:30am" | `30 8 * * 1-5` |
        | "every Friday at 6pm" | `0 18 * * 5` |

        For one-time tasks, use the `run_once` parameter with an ISO 8601 datetime instead of a cron expression.

        ### Cron Expression Format

        ```
        ┌───────────── minute (0-59)
        │ ┌───────────── hour (0-23)
        │ │ ┌───────────── day of month (1-31)
        │ │ │ ┌───────────── month (1-12)
        │ │ │ │ ┌───────────── day of week (0-6, Sunday=0)
        │ │ │ │ │
        * * * * *
        ```

        ### Timezone Handling

        **IMPORTANT**: Check the user's timezone from memory before scheduling.

        1. Call `memory_recall(userId, categories="preference,fact", query="timezone")` first
        2. If timezone is found, convert user's local time to UTC for the cron expression
        3. If timezone is unknown, ask the user or default to UTC

        Example:
        - User timezone: Europe/Madrid (UTC+1)
        - User says: "every day at 8pm"
        - 8pm Madrid = 7pm UTC → cron `0 19 * * *`

        ### Missed Execution Policies

        When creating a schedule, you can specify how to handle missed executions:

        - `skip_to_next`: Skip missed runs, wait for next scheduled time (default)
        - `run_immediately`: Execute all missed runs when service recovers
        - `run_once_if_missed`: Run once if any executions were missed (coalesce)

        ### Best Practices

        1. **Be specific in commands**: The scheduled task runs without conversation context,
           so the command should be self-contained and explicit.

        2. **Use descriptive names**: Help users identify their schedules at a glance.

        3. **Add relevant tags**: Use tags for categorization (e.g., "media", "daily", "backup").

        4. **Set appropriate limits**: Use `max_runs` for tasks that should stop after N executions.

        5. **Set expiration when appropriate**: Use `expires_at` for time-limited tasks.

        ### Example Interaction

        User: "Check for new episodes of my shows every evening at 8pm"

        1. First, check timezone: `memory_recall(userId, query="timezone")`
        2. Convert time to UTC based on user's timezone
        3. Create schedule:
           ```
           schedule_task(
             name="Daily TV Episode Check",
             description="Check for new episodes of tracked TV shows",
             command="Search for new episodes of my tracked shows and notify me",
             schedule="0 19 * * *",  // 8pm user time converted to UTC
             tags="media,tv,daily"
           )
           ```

        ### Execution Context

        Scheduled tasks execute with:
        - **Fresh context**: No conversation history from previous runs
        - **Auto-approved tools**: All tool calls are automatically approved
        - **Same user scope**: Access to user's memories and preferences
        - **Results logged**: Execution output is stored for review via `get_schedule`
        """;
}
