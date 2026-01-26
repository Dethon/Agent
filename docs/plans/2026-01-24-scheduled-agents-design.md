# Scheduled Agent Spawning Design

## Overview

Enable agents to create cron-like schedules that spawn other agents with prompts. Schedules are stored in Redis and executed by a background service that routes responses to Telegram or WebChat.

## Requirements

- **Recurring tasks**: "Check for new torrents matching X every day at 9am"
- **One-shot execution**: "Remind me about X tomorrow at 3pm"
- **Self-continuation**: Agent schedules itself to continue later
- **Multi-channel delivery**: Responses go to Telegram or WebChat topics
- **Topic handling**: Use existing topic or create new if invalid/missing

## Data Model

```csharp
// Domain/DTOs/Schedule.cs
public record Schedule
{
    public required string Id { get; init; }
    public required AgentDefinition Agent { get; init; }
    public required string Prompt { get; init; }
    public string? CronExpression { get; init; }          // Null for one-shot
    public DateTime? RunAt { get; init; }                 // For one-shots only
    public required ScheduleTarget Target { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextRunAt { get; init; }
}

public record ScheduleTarget
{
    public required string Channel { get; init; }         // "telegram" or "webchat"
    public long? ChatId { get; init; }
    public long? ThreadId { get; init; }
    public string? UserId { get; init; }
    public string? BotTokenHash { get; init; }
}
```

## Storage Layer

```csharp
// Domain/Contracts/IScheduleStore.cs
public interface IScheduleStore
{
    Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default);
    Task<Schedule?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default);
    Task UpdateLastRunAsync(string id, DateTime lastRunAt, DateTime? nextRunAt, CancellationToken ct = default);
}
```

**Redis key patterns:**
- `schedule:{id}` - JSON string containing schedule data
- `schedules` - SET of all schedule IDs (for listing)
- `schedules:due` - ZSET scored by `NextRunAt` ticks (for efficient polling)

One-shot schedules get TTL of `RunAt + 1 hour`.

## Domain Tools

Agents interact with schedules via three tools:

```csharp
// Domain/Tools/Scheduling/ScheduleCreateTool.cs
[Description("Creates a scheduled agent task. Use cron for recurring or runAt for one-shot.")]
public class ScheduleCreateTool(
    IScheduleStore store,
    ICronValidator validator,
    IAgentDefinitionProvider agentProvider)
{
    public async Task<ScheduleCreateResult> ExecuteAsync(
        string agentId,              // Resolved to full AgentDefinition
        string prompt,
        string? cronExpression,
        DateTime? runAt,
        ScheduleTarget target,
        CancellationToken ct);
}

// Domain/Tools/Scheduling/ScheduleListTool.cs
[Description("Lists all scheduled agent tasks.")]
public class ScheduleListTool(IScheduleStore store)
{
    public async Task<IReadOnlyList<ScheduleSummary>> ExecuteAsync(CancellationToken ct);
}

// Domain/Tools/Scheduling/ScheduleDeleteTool.cs
[Description("Deletes a scheduled agent task by ID.")]
public class ScheduleDeleteTool(IScheduleStore store)
{
    public async Task<bool> ExecuteAsync(string id, CancellationToken ct);
}
```

**Validation:**
- Either `cronExpression` OR `runAt` must be provided (not both, not neither)
- Cron expressions validated via `ICronValidator`
- `runAt` must be in the future
- `agentId` must resolve to a configured agent

## Architecture: Decoupled Dispatch and Execution

Uses a Channel to decouple schedule polling from execution, preventing long-running agents from blocking new schedule checks.

```csharp
// Domain/Monitor/ScheduleDispatcher.cs
public class ScheduleDispatcher(
    IScheduleStore store,
    Channel<Schedule> scheduleChannel,
    ILogger<ScheduleDispatcher> logger)
{
    public async Task DispatchDueSchedulesAsync(CancellationToken ct)
    {
        var dueSchedules = await store.GetDueSchedulesAsync(DateTime.UtcNow, ct);

        foreach (var schedule in dueSchedules)
        {
            // Mark as dispatched immediately to prevent re-dispatch
            var nextRun = schedule.CronExpression is not null
                ? CalculateNextRun(schedule)
                : (DateTime?)null;
            await store.UpdateLastRunAsync(schedule.Id, DateTime.UtcNow, nextRun, ct);

            await scheduleChannel.Writer.WriteAsync(schedule, ct);
        }
    }
}

// Domain/Monitor/ScheduleExecutor.cs
public class ScheduleExecutor(
    IScheduleStore store,
    IAgentFactory agentFactory,
    IChatMessengerClient messengerClient,
    Channel<Schedule> scheduleChannel,
    ILogger<ScheduleExecutor> logger)
{
    public async Task ProcessSchedulesAsync(CancellationToken ct)
    {
        await scheduleChannel.Reader
            .ReadAllAsync(ct)
            .Select(schedule => ProcessScheduleAsync(schedule, ct))
            .Merge(ct);
    }

    private async IAsyncEnumerable<bool> ProcessScheduleAsync(
        Schedule schedule,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var agentKey = await messengerClient.CreateTopicIfNeededAsync(
            schedule.Target.ChatId,
            schedule.Target.ThreadId,
            schedule.Target.UserId,
            schedule.Target.BotTokenHash,
            ct);

        await using var agent = agentFactory.CreateFromDefinition(
            agentKey,
            schedule.Target.UserId ?? "scheduler",
            schedule.Agent);

        var prompt = new ChatPrompt(agentKey, schedule.Prompt);

        await foreach (var chunk in agent.RunStreamingAsync(prompt, ct))
        {
            await messengerClient.SendResponseAsync(agentKey, chunk, ct);
            yield return true;
        }

        // Delete one-shots after completion
        if (schedule.CronExpression is null)
            await store.DeleteAsync(schedule.Id, ct);
    }
}
```

## Background Service

```csharp
// Agent/App/ScheduleMonitoring.cs
public class ScheduleMonitoring(
    ScheduleDispatcher dispatcher,
    ScheduleExecutor executor,
    Channel<Schedule> scheduleChannel) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var dispatchTask = RunDispatcherAsync(ct);
        var executeTask = executor.ProcessSchedulesAsync(ct);

        await Task.WhenAll(dispatchTask, executeTask);
    }

    private async Task RunDispatcherAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(30);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await dispatcher.DispatchDueSchedulesAsync(ct);
                await Task.Delay(interval, ct);
            }
        }
        finally
        {
            scheduleChannel.Writer.Complete();
        }
    }
}
```

## Target Thread Resolution

Uses existing `IChatMessengerClient.CreateTopicIfNeededAsync`, updated to validate topic existence:

- If `ThreadId` provided and valid: use existing topic
- If `ThreadId` missing or invalid: create new topic
- Works for both Telegram and WebChat

Implementations must be updated to check topic existence before returning.

## DI Configuration

```csharp
// Agent/Modules/SchedulingModule.cs
public static class SchedulingModule
{
    public static IServiceCollection AddScheduling(this IServiceCollection services)
    {
        services.AddSingleton(Channel.CreateUnbounded<Schedule>(
            new UnboundedChannelOptions { SingleReader = true }));

        services.AddSingleton<IScheduleStore, RedisScheduleStore>();
        services.AddSingleton<ICronValidator, CronValidator>();

        services.AddTransient<ScheduleCreateTool>();
        services.AddTransient<ScheduleListTool>();
        services.AddTransient<ScheduleDeleteTool>();

        services.AddSingleton<ScheduleDispatcher>();
        services.AddSingleton<ScheduleExecutor>();

        services.AddHostedService<ScheduleMonitoring>();

        return services;
    }
}
```

## Files to Create

| File | Purpose |
|------|---------|
| `Domain/DTOs/Schedule.cs` | Schedule and ScheduleTarget records |
| `Domain/Contracts/IScheduleStore.cs` | Storage interface |
| `Domain/Contracts/ICronValidator.cs` | Cron validation interface |
| `Domain/Contracts/IAgentDefinitionProvider.cs` | Agent lookup interface |
| `Domain/Tools/Scheduling/ScheduleCreateTool.cs` | Create schedule tool |
| `Domain/Tools/Scheduling/ScheduleListTool.cs` | List schedules tool |
| `Domain/Tools/Scheduling/ScheduleDeleteTool.cs` | Delete schedule tool |
| `Domain/Monitor/ScheduleDispatcher.cs` | Polls due schedules, writes to channel |
| `Domain/Monitor/ScheduleExecutor.cs` | Reads from channel, runs agents |
| `Infrastructure/StateManagers/RedisScheduleStore.cs` | Redis implementation |
| `Infrastructure/Validation/CronValidator.cs` | NCrontab wrapper |
| `Infrastructure/Agents/AgentDefinitionProvider.cs` | Wraps configured agents |
| `Agent/App/ScheduleMonitoring.cs` | BackgroundService host |
| `Agent/Modules/SchedulingModule.cs` | DI configuration |

## Files to Modify

| File | Change |
|------|--------|
| `Domain/Contracts/IThreadStateStore.cs` | Add `ExistsAsync` method |
| `Infrastructure/StateManagers/RedisThreadStateStore.cs` | Implement `ExistsAsync` |
| `Infrastructure/Clients/Messaging/TelegramChatClient.cs` | Check topic existence in `CreateTopicIfNeededAsync` |
| `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` | Check topic existence in `CreateTopicIfNeededAsync` |
| `Agent/Program.cs` | Add `services.AddScheduling()` |

## Design Decisions

1. **Native implementation over MCP**: Schedules need direct access to `MultiAgentFactory`, `IChatMessengerClient`, and BackgroundService hosting. MCP would require complex bridging.

2. **Full AgentDefinition in schedule**: Stored by resolving agent ID at creation time. Provides flexibility without coupling to runtime config changes.

3. **Cron format only in tools**: LLM agents convert natural language to cron. Tools validate but don't parse natural language.

4. **Channel-based decoupling**: Prevents long-running agent executions from blocking schedule polling.

5. **One-shots auto-delete**: After successful execution, one-shot schedules are removed. Thread history serves as execution record.

6. **No ownership restrictions**: Any agent can list/delete any schedule.
