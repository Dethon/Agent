# Scheduled Tasks Feature Specification

## Overview

The Scheduled Tasks feature enables users to schedule recurring or one-time tasks that the agent executes
automatically. Tasks are defined via natural language in Telegram chat and stored persistently in Redis.

## Goals

1. **User-Friendly Scheduling**: Natural language input converted to cron expressions
2. **Reliable Execution**: Distributed, fault-tolerant task execution with retry logic
3. **Visibility**: Users can list, inspect, pause, and cancel scheduled tasks
4. **Integration**: Scheduled tasks trigger the existing agent pipeline (tools, approval, memory)
5. **Isolation**: Tasks are scoped per user/chat with proper access control

## Non-Goals

- Sub-minute scheduling precision (minimum granularity: 1 minute)
- Complex workflow orchestration (use external tools for DAGs)
- Cross-user task sharing

---

## Architecture

### Layer Structure

```
Domain/
├── Contracts/
│   ├── IScheduler.cs
│   └── IScheduleStore.cs
├── DTOs/
│   ├── ScheduledTask.cs
│   ├── ScheduleRunLog.cs
│   └── ScheduleStatus.cs
└── Tools/Scheduling/
    ├── ScheduleTaskTool.cs
    ├── ListSchedulesTool.cs
    ├── GetScheduleTool.cs
    ├── PauseScheduleTool.cs
    └── CancelScheduleTool.cs

Infrastructure/
├── Schedulers/
│   ├── RedisScheduler.cs
│   └── SchedulerHostedService.cs
└── StateManagers/
    └── RedisScheduleStore.cs

McpServerScheduler/
├── Program.cs
├── Modules/
│   └── ConfigModule.cs
├── McpTools/
│   ├── McpScheduleTaskTool.cs
│   ├── McpListSchedulesTool.cs
│   ├── McpGetScheduleTool.cs
│   ├── McpPauseScheduleTool.cs
│   └── McpCancelScheduleTool.cs
├── McpPrompts/
│   └── McpSystemPrompt.cs
└── Settings/
    └── McpSettings.cs
```

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              Telegram User                                  │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                             ChatMonitor                                     │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ ReadPrompts → GroupByStreaming → ProcessChatThread → SendResponse   │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                          ┌───────────┴───────────┐
                          ▼                       ▼
              ┌─────────────────────┐   ┌─────────────────────┐
              │   MCP Tool Call     │   │ SchedulerHostedSvc  │
              │ (schedule_task)     │   │ (Background Runner) │
              └─────────────────────┘   └─────────────────────┘
                          │                       │
                          ▼                       ▼
              ┌─────────────────────────────────────────────────┐
              │              IScheduler                         │
              │  ┌─────────────────────────────────────────┐   │
              │  │ CreateAsync / UpdateAsync / DeleteAsync │   │
              │  │ GetDueTasksAsync / MarkExecutedAsync    │   │
              │  └─────────────────────────────────────────┘   │
              └─────────────────────────────────────────────────┘
                                      │
                                      ▼
              ┌─────────────────────────────────────────────────┐
              │              IScheduleStore (Redis)             │
              │  ┌─────────────────────────────────────────┐   │
              │  │ Tasks: schedule:{taskId} → JSON         │   │
              │  │ Index: user-schedules:{userId} → Set    │   │
              │  │ Queue: schedule-due → Sorted Set        │   │
              │  └─────────────────────────────────────────┘   │
              └─────────────────────────────────────────────────┘
```

---

## Data Models

### ScheduledTask

```csharp
namespace Domain.DTOs;

public record ScheduledTask
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required long ChatId { get; init; }
    public long? ThreadId { get; init; }

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Command { get; init; }

    /// <summary>
    /// Cron expression (5-part: minute hour day month weekday)
    /// Examples: "0 9 * * *" (daily 9am), "*/30 * * * *" (every 30 min)
    /// </summary>
    public required string CronExpression { get; init; }

    /// <summary>
    /// Optional: For one-time tasks, the exact execution time (UTC)
    /// </summary>
    public DateTimeOffset? RunOnce { get; init; }

    public ScheduleStatus Status { get; init; } = ScheduleStatus.Active;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRunAt { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }

    public int RunCount { get; init; }
    public int FailureCount { get; init; }
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Optional: Maximum number of executions (null = unlimited)
    /// </summary>
    public int? MaxRuns { get; init; }

    /// <summary>
    /// Optional: Task expires after this time (UTC)
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Tags for categorization and filtering
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Policy for handling missed executions (default: SkipToNext)
    /// </summary>
    public MissedExecutionPolicy MissedPolicy { get; init; } = MissedExecutionPolicy.SkipToNext;
}
```

### ScheduleStatus

```csharp
namespace Domain.DTOs;

public enum ScheduleStatus
{
    Active,
    Paused,
    Running,
    Completed,  // MaxRuns reached or RunOnce executed
    Failed,     // MaxRetries exceeded
    Expired,    // ExpiresAt passed
    Cancelled
}
```

### MissedExecutionPolicy

```csharp
namespace Domain.DTOs;

public enum MissedExecutionPolicy
{
    /// <summary>
    /// Execute all missed tasks as soon as service recovers.
    /// </summary>
    RunImmediately,

    /// <summary>
    /// Skip missed executions, wait for next scheduled time.
    /// </summary>
    SkipToNext,

    /// <summary>
    /// Run once if any executions were missed (coalesce multiple missed runs into one).
    /// </summary>
    RunOnceIfMissed
}
```

### ScheduleRunLog

```csharp
namespace Domain.DTOs;

public record ScheduleRunLog
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }

    public TimeSpan Duration => CompletedAt.HasValue
        ? CompletedAt.Value - StartedAt
        : TimeSpan.Zero;
}
```

---

## Contracts

### IScheduler

```csharp
namespace Domain.Contracts;

public interface IScheduler
{
    /// <summary>
    /// Creates a new scheduled task.
    /// </summary>
    Task<ScheduledTask> CreateAsync(ScheduledTask task, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing task (e.g., change cron, pause/resume).
    /// </summary>
    Task<ScheduledTask> UpdateAsync(ScheduledTask task, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes a task and its run history.
    /// </summary>
    Task<bool> DeleteAsync(string userId, string taskId, CancellationToken ct = default);

    /// <summary>
    /// Gets a task by ID (with user scope check).
    /// </summary>
    Task<ScheduledTask?> GetByIdAsync(string userId, string taskId, CancellationToken ct = default);

    /// <summary>
    /// Lists all tasks for a user, optionally filtered by status.
    /// </summary>
    Task<IReadOnlyList<ScheduledTask>> ListAsync(
        string userId,
        ScheduleStatus? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets tasks that are due for execution.
    /// </summary>
    Task<IReadOnlyList<ScheduledTask>> GetDueTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks a task as executed and updates next run time.
    /// </summary>
    Task<ScheduledTask> MarkExecutedAsync(
        string taskId,
        bool success,
        string? output = null,
        string? error = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets execution history for a task.
    /// </summary>
    Task<IReadOnlyList<ScheduleRunLog>> GetRunHistoryAsync(
        string userId,
        string taskId,
        int limit = 10,
        CancellationToken ct = default);
}
```

### IScheduleStore

```csharp
namespace Domain.Contracts;

public interface IScheduleStore
{
    Task SaveAsync(ScheduledTask task, CancellationToken ct = default);
    Task<ScheduledTask?> GetByIdAsync(string taskId, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledTask>> GetByUserIdAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledTask>> GetDueTasksAsync(DateTimeOffset asOf, CancellationToken ct = default);
    Task<bool> DeleteAsync(string taskId, CancellationToken ct = default);

    Task SaveRunLogAsync(ScheduleRunLog log, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduleRunLog>> GetRunLogsAsync(string taskId, int limit, CancellationToken ct = default);
}
```

---

## Tools

### schedule_task

Creates a new scheduled task.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| name | string | Yes | Short name for the task |
| description | string | Yes | What the task does |
| command | string | Yes | The command/instruction to execute |
| schedule | string | Yes | Cron expression OR natural language (e.g., "every day at 9am") |
| tags | string | No | Comma-separated tags for categorization |
| max_runs | int | No | Maximum executions (omit for unlimited) |
| expires_at | string | No | ISO 8601 datetime when task expires |
| missed_policy | string | No | Policy for missed executions: "run_immediately", "skip_to_next" (default), "run_once_if_missed" |

**Example Usage:**

```
User: "Remind me to check for new episodes of my shows every day at 8pm"

Tool call: schedule_task
  name: "Check TV Episodes"
  description: "Check for new episodes of tracked TV shows"
  command: "Search for new episodes of my tracked shows and notify me of any new releases"
  schedule: "0 20 * * *"
  tags: "media,tv,daily"
```

**Response:**

```json
{
  "status": "created",
  "task": {
    "id": "sched_abc123",
    "name": "Check TV Episodes",
    "nextRunAt": "2024-01-15T20:00:00Z",
    "cronExpression": "0 20 * * *"
  }
}
```

### list_schedules

Lists all scheduled tasks for the user.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| status | string | No | Filter by status: active, paused, completed, failed |
| tag | string | No | Filter by tag |

**Response:**

```json
{
  "tasks": [
    {
      "id": "sched_abc123",
      "name": "Check TV Episodes",
      "status": "active",
      "schedule": "0 20 * * *",
      "nextRunAt": "2024-01-15T20:00:00Z",
      "lastRunAt": "2024-01-14T20:00:00Z",
      "runCount": 5
    }
  ],
  "total": 1
}
```

### get_schedule

Gets details of a specific scheduled task including recent run history.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| task_id | string | Yes | The task ID |

**Response:**

```json
{
  "task": {
    "id": "sched_abc123",
    "name": "Check TV Episodes",
    "description": "Check for new episodes of tracked TV shows",
    "command": "Search for new episodes...",
    "cronExpression": "0 20 * * *",
    "status": "active",
    "createdAt": "2024-01-10T10:00:00Z",
    "lastRunAt": "2024-01-14T20:00:00Z",
    "nextRunAt": "2024-01-15T20:00:00Z",
    "runCount": 5,
    "failureCount": 0
  },
  "recentRuns": [
    {
      "id": "run_xyz789",
      "startedAt": "2024-01-14T20:00:00Z",
      "completedAt": "2024-01-14T20:00:15Z",
      "success": true,
      "output": "Found 2 new episodes..."
    }
  ]
}
```

### pause_schedule

Pauses or resumes a scheduled task.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| task_id | string | Yes | The task ID |
| paused | bool | Yes | true to pause, false to resume |

### cancel_schedule

Permanently cancels and deletes a scheduled task.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| task_id | string | Yes | The task ID |

---

## Redis Data Schema

### Keys

| Key Pattern | Type | TTL | Description |
|-------------|------|-----|-------------|
| `schedule:{taskId}` | String (JSON) | None | Task definition |
| `user-schedules:{userId}` | Set | None | Set of taskIds for user |
| `schedule-due` | Sorted Set | None | Score = next run timestamp |
| `schedule-runs:{taskId}` | List | 30 days | Recent run logs (capped) |
| `schedule-lock:{taskId}` | String | 5 min | Distributed lock for execution |

### Task Storage Example

```redis
SET schedule:sched_abc123 '{
  "id": "sched_abc123",
  "userId": "user_123",
  "chatId": 456789,
  "name": "Check TV Episodes",
  "description": "Check for new episodes",
  "command": "Search for new episodes...",
  "cronExpression": "0 20 * * *",
  "status": "active",
  "createdAt": "2024-01-10T10:00:00Z",
  "nextRunAt": "2024-01-15T20:00:00Z",
  "runCount": 5
}'

SADD user-schedules:user_123 sched_abc123

ZADD schedule-due 1705348800 sched_abc123
```

---

## Execution Flow

### Task Creation

```
1. User: "Schedule a check for new movies every Friday at 6pm"
2. LLM parses natural language → cron expression "0 18 * * 5"
3. Tool call: schedule_task(...)
4. Scheduler.CreateAsync():
   a. Generate unique taskId
   b. Calculate NextRunAt from cron
   c. Save to Redis (task + user index + due queue)
5. Return confirmation to user
```

### Task Execution (SchedulerHostedService)

```
1. Poll GetDueTasksAsync() every 30 seconds
2. For each due task:
   a. Acquire distributed lock (schedule-lock:{taskId})
   b. Check for missed executions:
      - If LastRunAt + interval < now, apply MissedExecutionPolicy
      - RunImmediately: queue all missed runs
      - SkipToNext: skip to current execution
      - RunOnceIfMissed: execute once if any missed
   c. Update status to Running
   d. Create fresh agent context (no conversation history)
   e. Create synthetic ChatPrompt:
      - ChatId/ThreadId from task
      - Text = task.Command
      - IsScheduled = true
      - AutoApproveTools = true
   f. Execute via dedicated agent instance
   g. Wait for completion (with timeout)
   h. Log result to schedule-runs:{taskId}
   i. Calculate next run time, update task
   j. Release lock
```

### Fresh Execution Context

Each scheduled task execution starts with a clean slate:

```csharp
// No conversation history loaded - fresh context each run
var agent = agentFactory.Create(agentKey, task.UserId);
agent.SetAutoApproveAllTools(true);  // Bypass approval for scheduled runs

// Execute with fresh thread (no prior messages)
var response = await agent.ExecuteAsync(
    task.Command,
    messages: [],  // Empty history
    ct);
```

### Synthetic Prompt Structure

```csharp
var prompt = new ChatPrompt
{
    ChatId = task.ChatId,
    ThreadId = task.ThreadId,
    UserId = task.UserId,
    Text = task.Command,
    MessageId = 0,  // Synthetic
    Metadata = new Dictionary<string, string>
    {
        ["scheduled_task_id"] = task.Id,
        ["scheduled_task_name"] = task.Name,
        ["is_scheduled"] = "true",
        ["auto_approve_tools"] = "true"  // Bypass approval system
    }
};
```

---

## Timezone Handling

### Storage
All times are stored in UTC internally. The `ScheduledTask.NextRunAt`, `LastRunAt`, `CreatedAt`,
and `ExpiresAt` fields are all `DateTimeOffset` in UTC.

### User Timezone Detection
The user's timezone is retrieved from the Memory MCP via the `memory_recall` tool:

```csharp
// Query memory for user's timezone preference
var memories = await memoryStore.SearchAsync(
    userId,
    query: "timezone",
    categories: [MemoryCategory.Preference, MemoryCategory.Fact],
    limit: 1,
    ct: ct);

var userTimezone = memories.FirstOrDefault()?.Content ?? "UTC";
// e.g., "Europe/Madrid", "America/New_York", "Asia/Tokyo"
```

### Display Conversion
When displaying times to users, convert from UTC to their local timezone:

```csharp
public static string FormatForUser(DateTimeOffset utcTime, string timezoneId)
{
    var tz = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
    var localTime = TimeZoneInfo.ConvertTime(utcTime, tz);
    return localTime.ToString("dddd, MMMM d 'at' h:mm tt");
    // e.g., "Friday, January 15 at 8:00 PM"
}
```

### Schedule Input
When users specify times in natural language (e.g., "every day at 8pm"), the LLM should:
1. Query memory for the user's timezone
2. Interpret the time in that timezone
3. Convert to UTC for the cron expression

Example system prompt addition:
```
When scheduling tasks, first check the user's timezone from memory. If unknown,
ask the user or default to UTC. Convert user-specified times to UTC for storage.

User timezone: Europe/Madrid (UTC+1)
User says: "every day at 8pm"
→ 8pm Madrid = 7pm UTC = cron "0 19 * * *"
```

---

## Natural Language Parsing

The LLM handles natural language to cron conversion. System prompt guidance:

```
When the user wants to schedule a task, convert their natural language
schedule description to a 5-part cron expression:

Examples:
- "every day at 9am" → "0 9 * * *"
- "every Monday at 10am" → "0 10 * * 1"
- "every hour" → "0 * * * *"
- "every 30 minutes" → "*/30 * * * *"
- "first day of every month at midnight" → "0 0 1 * *"
- "weekdays at 8:30am" → "30 8 * * 1-5"

For one-time tasks, use the run_once parameter with ISO 8601 datetime
instead of a cron expression.
```

---

## Tool Approval Integration

Scheduled task management tools require approval:

| Tool | Default Approval |
|------|------------------|
| schedule_task | Required |
| list_schedules | Auto-approved |
| get_schedule | Auto-approved |
| pause_schedule | Required |
| cancel_schedule | Required |

**Scheduled task execution**: All tool calls within scheduled task execution are **auto-approved**.
This enables unattended operation - the user approves once when creating the schedule, and all
subsequent executions run without intervention.

---

## Error Handling

### Retry Strategy

```csharp
public class RetryPolicy
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(1);
    public double BackoffMultiplier { get; init; } = 2.0;

    public TimeSpan GetDelay(int attempt) =>
        TimeSpan.FromTicks((long)(InitialDelay.Ticks * Math.Pow(BackoffMultiplier, attempt - 1)));
}
```

### Failure States

| Scenario | Action |
|----------|--------|
| Task throws exception | Log error, increment failureCount, retry if under limit |
| Timeout (5 min default) | Cancel, log timeout, retry |
| MaxRetries exceeded | Set status = Failed, notify user |
| Lock acquisition fails | Skip this cycle, will retry next poll |

### User Notifications

On failure, send a Telegram message:

```
⚠️ Scheduled task "{task.Name}" failed after {maxRetries} attempts.

Last error: {error.Message}

Use /schedules to view details or reschedule.
```

---

## MCP Server Configuration

### McpSettings

```csharp
public class McpSettings
{
    public required string RedisConnectionString { get; init; }
    public int PollIntervalSeconds { get; init; } = 30;
    public int TaskTimeoutMinutes { get; init; } = 5;
    public int MaxConcurrentTasks { get; init; } = 10;
    public int RunHistoryRetentionDays { get; init; } = 30;
}
```

### Docker Compose Addition

```yaml
mcp-scheduler:
  build:
    context: .
    dockerfile: McpServerScheduler/Dockerfile
  environment:
    - MCP__RedisConnectionString=redis:6379
    - MCP__PollIntervalSeconds=30
  depends_on:
    - redis
  networks:
    - agent-network
```

---

## Example Conversations

### Creating a Media Check Schedule

```
User: I want to check for new episodes of my shows every evening

Agent: I'll set up a daily check for new TV show episodes. What time
       would you like me to check? (e.g., 7pm, 8pm, 9pm)

User: 8pm

Agent: [Calls schedule_task]
       ✓ Created schedule "Daily TV Episode Check"

       Schedule: Every day at 8:00 PM
       Next run: Today at 8:00 PM

       I'll search for new episodes of your tracked shows and let you
       know if anything new is available.
```

### Listing and Managing Schedules

```
User: What schedules do I have?

Agent: [Calls list_schedules]
       You have 3 scheduled tasks:

       1. Daily TV Episode Check (active)
          Every day at 8:00 PM | Last run: Yesterday | Runs: 12

       2. Weekly Movie Search (active)
          Every Friday at 6:00 PM | Last run: 5 days ago | Runs: 4

       3. Torrent Health Check (paused)
          Every 6 hours | Paused since Jan 10

       Would you like to view details, pause, or cancel any of these?
```

### One-Time Reminder

```
User: Remind me to download that documentary tomorrow at noon

Agent: [Calls schedule_task with run_once]
       ✓ Created one-time reminder

       Task: Download documentary reminder
       Scheduled for: Tomorrow at 12:00 PM (in 18 hours)

       I'll send you a message at that time.
```

---

## Testing Strategy

### Unit Tests

- Cron expression parsing and next-run calculation
- ScheduledTask validation (invalid cron, missing fields)
- Status transitions (Active → Running → Active/Failed)
- Retry delay calculations

### Integration Tests

- Redis store operations (CRUD + queries)
- Distributed locking behavior
- Task execution end-to-end with mock agent
- Concurrent task execution limits

### Test Fixtures

```csharp
public class SchedulerFixture : IAsyncLifetime
{
    public IScheduler Scheduler { get; private set; }
    public IScheduleStore Store { get; private set; }

    public async Task InitializeAsync()
    {
        var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        Store = new RedisScheduleStore(redis);
        Scheduler = new RedisScheduler(Store, NullLogger<RedisScheduler>.Instance);
    }
}
```

---

## Migration & Rollout

### Phase 1: Core Infrastructure

1. Add Domain contracts and DTOs
2. Implement RedisScheduleStore
3. Implement RedisScheduler
4. Add SchedulerHostedService to Agent

### Phase 2: MCP Server

1. Create McpServerScheduler project
2. Implement MCP tools wrapping domain tools
3. Add system prompt for scheduling
4. Register in docker-compose

### Phase 3: Integration

1. Wire up synthetic prompt emission
2. Add tool approval configuration
3. Implement user notifications
4. Add to main agent's MCP endpoints

### Phase 4: Polish

1. Natural language schedule parsing improvements
2. Schedule visualization (timeline view)
3. Bulk operations (pause all, export schedules)
4. Schedule templates for common patterns

---

## Design Decisions

1. **Timezone handling**: All times stored in UTC internally. User's timezone is detected via the Memory MCP
   (stored as a user preference/fact). Display times are converted to user's local timezone in responses.

2. **Missed executions**: Configurable per task via `MissedExecutionPolicy`:
   - `RunImmediately` - Execute missed tasks as soon as service recovers
   - `SkipToNext` - Skip missed executions, wait for next scheduled time
   - `RunOnceIfMissed` - Run once if any executions were missed (coalesce)

3. **Execution context**: Fresh context for each execution. Scheduled tasks do not have access to
   conversation history - they start with a clean slate each run.

4. **Rate limiting**: No rate limits. Users can create unlimited schedules with any interval.

5. **Approval for scheduled runs**: Auto-approved. All tool calls within scheduled task execution
   bypass the approval system to enable unattended operation.

---

## Appendix: Cron Expression Reference

```
┌───────────── minute (0-59)
│ ┌───────────── hour (0-23)
│ │ ┌───────────── day of month (1-31)
│ │ │ ┌───────────── month (1-12)
│ │ │ │ ┌───────────── day of week (0-6, Sunday=0)
│ │ │ │ │
* * * * *
```

| Expression | Description |
|------------|-------------|
| `0 * * * *` | Every hour at minute 0 |
| `*/15 * * * *` | Every 15 minutes |
| `0 9 * * *` | Daily at 9:00 AM |
| `0 9 * * 1-5` | Weekdays at 9:00 AM |
| `0 0 1 * *` | First of every month at midnight |
| `0 18 * * 5` | Every Friday at 6:00 PM |
