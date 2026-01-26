# Scheduling: Remove Channel Concept

## Problem

The scheduling tools use a `channel` parameter to specify delivery mechanism ("telegram" or "webchat"). This design has two issues:

1. **Coupling to transport** - The schedule knows too much about delivery mechanisms
2. **Separation of concerns** - Scheduling should only care about *what* to run, not *where* to send results

## Decision

- Remove the `channel` concept entirely
- Notifications always go to WebChat (if configured)
- Schedules can still be created from any transport (Telegram, WebChat, CLI)

## Design

### Flattened Schedule DTO

Remove `ScheduleTarget` wrapper, flatten `UserId` directly onto `Schedule`:

```csharp
public record Schedule
{
    public required string Id { get; init; }
    public required AgentDefinition Agent { get; init; }
    public required string Prompt { get; init; }
    public string? CronExpression { get; init; }
    public DateTime? RunAt { get; init; }
    public string? UserId { get; init; }  // Optional user context
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextRunAt { get; init; }
}
```

Delete `ScheduleTarget` record entirely.

### ScheduleCreateTool Parameters

**Keep:**
- `agentId` (required)
- `prompt` (required)
- `cronExpression` (optional, mutually exclusive with runAt)
- `runAt` (optional, mutually exclusive with cronExpression)
- `userId` (optional) - user context for the prompt

**Remove:**
- `channel`
- `chatId`
- `threadId`
- `targetAgentId`

### ScheduleExecutor Behavior

1. Execute the agent with the scheduled prompt (always happens)
2. Type-check the injected `IChatMessengerClient`:
   - **Is `WebChatMessengerClient`**: Create topic and stream responses
   - **Is not**: Execute agent silently (no response delivery)

```csharp
await agent.ExecuteAsync(prompt, cancellationToken);

if (messengerClient is WebChatMessengerClient webChat)
{
    var topic = await webChat.CreateTopicIfNeededAsync(schedule.Agent.Id, ...);
    await webChat.ProcessResponseStreamAsync(response, topic);
}
```

### ScheduleSummary

```csharp
public record ScheduleSummary(
    string Id,
    string AgentName,
    string Prompt,
    string? CronExpression,
    DateTime? RunAt,
    DateTime? NextRunAt,
    string? UserId);
```

### Migration

Breaking change - existing schedules in Redis will be incompatible. Clear and recreate.

## Files to Modify

| File | Change |
|------|--------|
| `Domain/DTOs/Schedule.cs` | Remove `ScheduleTarget`, flatten `UserId`, update `ScheduleSummary` |
| `Domain/Tools/Scheduling/ScheduleCreateTool.cs` | Remove channel/chatId/threadId/targetAgentId params |
| `Domain/Tools/Scheduling/ScheduleListTool.cs` | Update output to show `UserId` instead of `Channel` |
| `Domain/Monitor/ScheduleExecutor.cs` | Type-check for `WebChatMessengerClient` |
| `Infrastructure/StateManagers/RedisScheduleStore.cs` | Update serialization |
| `Tests/**/*` | Update for new model |

## Files Unchanged

- `ScheduleDeleteTool.cs` - just deletes by ID
- `ScheduleDispatcher.cs` - doesn't touch target/channel
- `SchedulingModule.cs` - DI registration unchanged
