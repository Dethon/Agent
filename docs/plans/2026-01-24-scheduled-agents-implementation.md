# Scheduled Agent Spawning Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable agents to create cron-like schedules that spawn other agents with prompts, with responses delivered to Telegram or WebChat.

**Architecture:** Decoupled dispatcher/executor using System.Threading.Channels. Dispatcher polls Redis every 30s for due schedules and writes to channel. Executor reads from channel and runs agents concurrently using merge pipeline (same pattern as ChatMonitor).

**Tech Stack:** .NET 10, Redis (StackExchange.Redis), NCrontab for cron parsing, System.Threading.Channels

---

## Task 1: Add NCrontab Package

**Files:**
- Modify: `Domain/Domain.csproj`

**Step 1: Add NCrontab package reference**

```bash
cd G:\repos\agent\Domain && dotnet add package NCrontab
```

**Step 2: Verify package added**

Run: `dotnet list G:\repos\agent\Domain\Domain.csproj package | grep -i cron`
Expected: NCrontab listed

**Step 3: Commit**

```bash
git add Domain/Domain.csproj
git commit -m "chore: add NCrontab package for cron expression parsing"
```

---

## Task 2: Create Schedule DTOs

**Files:**
- Create: `Domain/DTOs/Schedule.cs`

**Step 1: Create Schedule and ScheduleTarget records**

```csharp
namespace Domain.DTOs;

public record Schedule
{
    public required string Id { get; init; }
    public required AgentDefinition Agent { get; init; }
    public required string Prompt { get; init; }
    public string? CronExpression { get; init; }
    public DateTime? RunAt { get; init; }
    public required ScheduleTarget Target { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextRunAt { get; init; }
}

public record ScheduleTarget
{
    public required string Channel { get; init; }
    public long? ChatId { get; init; }
    public long? ThreadId { get; init; }
    public string? UserId { get; init; }
    public string? BotTokenHash { get; init; }
}

public record ScheduleSummary(
    string Id,
    string AgentName,
    string Prompt,
    string? CronExpression,
    DateTime? RunAt,
    DateTime? NextRunAt,
    string Channel);
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/DTOs/Schedule.cs
git commit -m "feat(domain): add Schedule and ScheduleTarget DTOs"
```

---

## Task 3: Create IScheduleStore Interface

**Files:**
- Create: `Domain/Contracts/IScheduleStore.cs`

**Step 1: Create the interface**

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

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

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Contracts/IScheduleStore.cs
git commit -m "feat(domain): add IScheduleStore interface"
```

---

## Task 4: Create ICronValidator Interface

**Files:**
- Create: `Domain/Contracts/ICronValidator.cs`

**Step 1: Create the interface**

```csharp
namespace Domain.Contracts;

public interface ICronValidator
{
    bool IsValid(string cronExpression);
    DateTime? GetNextOccurrence(string cronExpression, DateTime from);
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Contracts/ICronValidator.cs
git commit -m "feat(domain): add ICronValidator interface"
```

---

## Task 5: Create IAgentDefinitionProvider Interface

**Files:**
- Create: `Domain/Contracts/IAgentDefinitionProvider.cs`

**Step 1: Create the interface**

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgentDefinitionProvider
{
    AgentDefinition? GetById(string agentId);
    IReadOnlyList<AgentDefinition> GetAll();
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Contracts/IAgentDefinitionProvider.cs
git commit -m "feat(domain): add IAgentDefinitionProvider interface"
```

---

## Task 6: Add ExistsAsync to IThreadStateStore

**Files:**
- Modify: `Domain/Contracts/IThreadStateStore.cs`

**Step 1: Add ExistsAsync method to interface**

Add this method to the `IThreadStateStore` interface:

```csharp
Task<bool> ExistsAsync(string key, CancellationToken ct = default);
```

The interface should now include:
```csharp
using Domain.Agents;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IThreadStateStore
{
    Task DeleteAsync(AgentKey key);
    Task<ChatMessage[]?> GetMessagesAsync(string key);
    Task SetMessagesAsync(string key, ChatMessage[] messages);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync();
    Task SaveTopicAsync(TopicMetadata topic);
    Task DeleteTopicAsync(string topicId);
}
```

**Step 2: Verify build fails (implementation missing)**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build fails - RedisThreadStateStore doesn't implement ExistsAsync

**Step 3: Commit interface change**

```bash
git add Domain/Contracts/IThreadStateStore.cs
git commit -m "feat(domain): add ExistsAsync to IThreadStateStore"
```

---

## Task 7: Implement ExistsAsync in RedisThreadStateStore

**Files:**
- Modify: `Infrastructure/StateManagers/RedisThreadStateStore.cs`

**Step 1: Add ExistsAsync implementation**

Add this method to `RedisThreadStateStore`:

```csharp
public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
{
    return await _db.KeyExistsAsync(key);
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Infrastructure/StateManagers/RedisThreadStateStore.cs
git commit -m "feat(infrastructure): implement ExistsAsync in RedisThreadStateStore"
```

---

## Task 8: Create CronValidator Implementation

**Files:**
- Create: `Infrastructure/Validation/CronValidator.cs`

**Step 1: Create the implementation**

```csharp
using Domain.Contracts;
using NCrontab;

namespace Infrastructure.Validation;

public class CronValidator : ICronValidator
{
    public bool IsValid(string cronExpression)
    {
        var result = CrontabSchedule.TryParse(cronExpression);
        return result is not null;
    }

    public DateTime? GetNextOccurrence(string cronExpression, DateTime from)
    {
        var schedule = CrontabSchedule.TryParse(cronExpression);
        return schedule?.GetNextOccurrence(from);
    }
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Infrastructure/Validation/CronValidator.cs
git commit -m "feat(infrastructure): add CronValidator using NCrontab"
```

---

## Task 9: Create AgentDefinitionProvider Implementation

**Files:**
- Create: `Infrastructure/Agents/AgentDefinitionProvider.cs`

**Step 1: Create the implementation**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Options;

namespace Infrastructure.Agents;

public class AgentDefinitionProvider(IOptionsMonitor<AgentRegistryOptions> registryOptions)
    : IAgentDefinitionProvider
{
    public AgentDefinition? GetById(string agentId)
    {
        return registryOptions.CurrentValue.Agents
            .FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<AgentDefinition> GetAll()
    {
        return registryOptions.CurrentValue.Agents;
    }
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Infrastructure/Agents/AgentDefinitionProvider.cs
git commit -m "feat(infrastructure): add AgentDefinitionProvider"
```

---

## Task 10: Create RedisScheduleStore Implementation

**Files:**
- Create: `Infrastructure/StateManagers/RedisScheduleStore.cs`

**Step 1: Create the implementation**

```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisScheduleStore(IConnectionMultiplexer redis) : IScheduleStore
{
    private const string ScheduleSetKey = "schedules";
    private const string DueSetKey = "schedules:due";
    private static readonly TimeSpan OneShotBuffer = TimeSpan.FromHours(1);

    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default)
    {
        var key = ScheduleKey(schedule.Id);
        var json = JsonSerializer.Serialize(schedule);

        var transaction = _db.CreateTransaction();

        _ = transaction.StringSetAsync(key, json);
        _ = transaction.SetAddAsync(ScheduleSetKey, schedule.Id);

        if (schedule.NextRunAt.HasValue)
        {
            _ = transaction.SortedSetAddAsync(DueSetKey, schedule.Id, schedule.NextRunAt.Value.Ticks);
        }

        if (schedule.CronExpression is null && schedule.RunAt.HasValue)
        {
            _ = transaction.KeyExpireAsync(key, schedule.RunAt.Value.Add(OneShotBuffer) - DateTime.UtcNow);
        }

        await transaction.ExecuteAsync();

        return schedule;
    }

    public async Task<Schedule?> GetAsync(string id, CancellationToken ct = default)
    {
        var json = await _db.StringGetAsync(ScheduleKey(id));
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Schedule>(json!);
    }

    public async Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken ct = default)
    {
        var ids = await _db.SetMembersAsync(ScheduleSetKey);
        var schedules = new List<Schedule>();

        foreach (var id in ids)
        {
            var schedule = await GetAsync(id!);
            if (schedule is not null)
            {
                schedules.Add(schedule);
            }
        }

        return schedules.OrderBy(s => s.NextRunAt ?? DateTime.MaxValue).ToList();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();

        _ = transaction.KeyDeleteAsync(ScheduleKey(id));
        _ = transaction.SetRemoveAsync(ScheduleSetKey, id);
        _ = transaction.SortedSetRemoveAsync(DueSetKey, id);

        await transaction.ExecuteAsync();
    }

    public async Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default)
    {
        var dueIds = await _db.SortedSetRangeByScoreAsync(
            DueSetKey,
            stop: asOf.Ticks,
            take: 100);

        var schedules = new List<Schedule>();

        foreach (var id in dueIds)
        {
            var schedule = await GetAsync(id!);
            if (schedule is not null)
            {
                schedules.Add(schedule);
            }
        }

        return schedules;
    }

    public async Task UpdateLastRunAsync(string id, DateTime lastRunAt, DateTime? nextRunAt, CancellationToken ct = default)
    {
        var schedule = await GetAsync(id, ct);
        if (schedule is null)
        {
            return;
        }

        var updated = schedule with
        {
            LastRunAt = lastRunAt,
            NextRunAt = nextRunAt
        };

        var json = JsonSerializer.Serialize(updated);

        var transaction = _db.CreateTransaction();

        _ = transaction.StringSetAsync(ScheduleKey(id), json);

        if (nextRunAt.HasValue)
        {
            _ = transaction.SortedSetAddAsync(DueSetKey, id, nextRunAt.Value.Ticks);
        }
        else
        {
            _ = transaction.SortedSetRemoveAsync(DueSetKey, id);
        }

        await transaction.ExecuteAsync();
    }

    private static string ScheduleKey(string id) => $"schedule:{id}";
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Infrastructure/StateManagers/RedisScheduleStore.cs
git commit -m "feat(infrastructure): add RedisScheduleStore implementation"
```

---

## Task 11: Create ScheduleCreateTool

**Files:**
- Create: `Domain/Tools/Scheduling/ScheduleCreateTool.cs`

**Step 1: Create the tool**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling;

public class ScheduleCreateTool(
    IScheduleStore store,
    ICronValidator cronValidator,
    IAgentDefinitionProvider agentProvider)
{
    protected const string Name = "schedule_create";

    protected const string Description = """
        Creates a scheduled agent task. The specified agent will run with the given prompt
        at the scheduled time(s).

        For recurring schedules, use cronExpression (standard 5-field cron format):
        - "0 9 * * *" = every day at 9:00 AM
        - "0 */2 * * *" = every 2 hours
        - "30 14 * * 1-5" = weekdays at 2:30 PM

        For one-time schedules, use runAt with a UTC datetime.

        The target specifies where responses will be delivered (telegram or webchat).
        """;

    protected async Task<JsonNode> Run(
        string agentId,
        string prompt,
        string? cronExpression,
        DateTime? runAt,
        string channel,
        long? chatId,
        long? threadId,
        string? userId,
        string? botTokenHash,
        CancellationToken ct = default)
    {
        var validationError = Validate(agentId, cronExpression, runAt, channel);
        if (validationError is not null)
        {
            return validationError;
        }

        var agentDefinition = agentProvider.GetById(agentId);
        if (agentDefinition is null)
        {
            return new JsonObject { ["error"] = $"Agent '{agentId}' not found" };
        }

        var nextRunAt = CalculateNextRunAt(cronExpression, runAt);

        var schedule = new Schedule
        {
            Id = $"sched_{Guid.NewGuid():N}",
            Agent = agentDefinition,
            Prompt = prompt,
            CronExpression = cronExpression,
            RunAt = runAt,
            Target = new ScheduleTarget
            {
                Channel = channel,
                ChatId = chatId,
                ThreadId = threadId,
                UserId = userId,
                BotTokenHash = botTokenHash
            },
            CreatedAt = DateTime.UtcNow,
            NextRunAt = nextRunAt
        };

        await store.CreateAsync(schedule, ct);

        return new JsonObject
        {
            ["status"] = "created",
            ["scheduleId"] = schedule.Id,
            ["agentName"] = agentDefinition.Name,
            ["nextRunAt"] = nextRunAt?.ToString("O")
        };
    }

    private JsonObject? Validate(string agentId, string? cronExpression, DateTime? runAt, string channel)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return new JsonObject { ["error"] = "agentId is required" };
        }

        if (cronExpression is null && runAt is null)
        {
            return new JsonObject { ["error"] = "Either cronExpression or runAt must be provided" };
        }

        if (cronExpression is not null && runAt is not null)
        {
            return new JsonObject { ["error"] = "Provide only cronExpression OR runAt, not both" };
        }

        if (cronExpression is not null && !cronValidator.IsValid(cronExpression))
        {
            return new JsonObject { ["error"] = $"Invalid cron expression: {cronExpression}" };
        }

        if (runAt is not null && runAt <= DateTime.UtcNow)
        {
            return new JsonObject { ["error"] = "runAt must be in the future" };
        }

        if (channel is not "telegram" and not "webchat")
        {
            return new JsonObject { ["error"] = "channel must be 'telegram' or 'webchat'" };
        }

        return null;
    }

    private DateTime? CalculateNextRunAt(string? cronExpression, DateTime? runAt)
    {
        if (runAt.HasValue)
        {
            return runAt.Value;
        }

        if (cronExpression is not null)
        {
            return cronValidator.GetNextOccurrence(cronExpression, DateTime.UtcNow);
        }

        return null;
    }
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Tools/Scheduling/ScheduleCreateTool.cs
git commit -m "feat(domain): add ScheduleCreateTool"
```

---

## Task 12: Create ScheduleListTool

**Files:**
- Create: `Domain/Tools/Scheduling/ScheduleListTool.cs`

**Step 1: Create the tool**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling;

public class ScheduleListTool(IScheduleStore store)
{
    protected const string Name = "schedule_list";

    protected const string Description = """
        Lists all scheduled agent tasks. Shows schedule ID, agent name, prompt preview,
        schedule timing (cron or one-shot), next run time, and target channel.
        """;

    protected async Task<JsonNode> Run(CancellationToken ct = default)
    {
        var schedules = await store.ListAsync(ct);

        var summaries = schedules
            .Select(s => new ScheduleSummary(
                s.Id,
                s.Agent.Name,
                TruncatePrompt(s.Prompt),
                s.CronExpression,
                s.RunAt,
                s.NextRunAt,
                s.Target.Channel))
            .ToList();

        return new JsonObject
        {
            ["count"] = summaries.Count,
            ["schedules"] = new JsonArray(summaries.Select(ToJson).ToArray())
        };
    }

    private static string TruncatePrompt(string prompt)
    {
        const int maxLength = 100;
        return prompt.Length <= maxLength ? prompt : $"{prompt[..maxLength]}...";
    }

    private static JsonNode ToJson(ScheduleSummary summary)
    {
        var node = new JsonObject
        {
            ["id"] = summary.Id,
            ["agentName"] = summary.AgentName,
            ["prompt"] = summary.Prompt,
            ["channel"] = summary.Channel
        };

        if (summary.CronExpression is not null)
        {
            node["cronExpression"] = summary.CronExpression;
        }

        if (summary.RunAt.HasValue)
        {
            node["runAt"] = summary.RunAt.Value.ToString("O");
        }

        if (summary.NextRunAt.HasValue)
        {
            node["nextRunAt"] = summary.NextRunAt.Value.ToString("O");
        }

        return node;
    }
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Tools/Scheduling/ScheduleListTool.cs
git commit -m "feat(domain): add ScheduleListTool"
```

---

## Task 13: Create ScheduleDeleteTool

**Files:**
- Create: `Domain/Tools/Scheduling/ScheduleDeleteTool.cs`

**Step 1: Create the tool**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Scheduling;

public class ScheduleDeleteTool(IScheduleStore store)
{
    protected const string Name = "schedule_delete";

    protected const string Description = """
        Deletes a scheduled agent task by ID. Use schedule_list to find schedule IDs.
        """;

    protected async Task<JsonNode> Run(string scheduleId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
        {
            return new JsonObject { ["error"] = "scheduleId is required" };
        }

        var existing = await store.GetAsync(scheduleId, ct);
        if (existing is null)
        {
            return new JsonObject
            {
                ["status"] = "not_found",
                ["scheduleId"] = scheduleId
            };
        }

        await store.DeleteAsync(scheduleId, ct);

        return new JsonObject
        {
            ["status"] = "deleted",
            ["scheduleId"] = scheduleId,
            ["agentName"] = existing.Agent.Name
        };
    }
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Tools/Scheduling/ScheduleDeleteTool.cs
git commit -m "feat(domain): add ScheduleDeleteTool"
```

---

## Task 14: Create ScheduleDispatcher

**Files:**
- Create: `Domain/Monitor/ScheduleDispatcher.cs`

**Step 1: Create the dispatcher**

```csharp
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ScheduleDispatcher(
    IScheduleStore store,
    ICronValidator cronValidator,
    Channel<Schedule> scheduleChannel,
    ILogger<ScheduleDispatcher> logger)
{
    public async Task DispatchDueSchedulesAsync(CancellationToken ct)
    {
        try
        {
            var dueSchedules = await store.GetDueSchedulesAsync(DateTime.UtcNow, ct);

            foreach (var schedule in dueSchedules)
            {
                var nextRun = CalculateNextRun(schedule);
                await store.UpdateLastRunAsync(schedule.Id, DateTime.UtcNow, nextRun, ct);

                await scheduleChannel.Writer.WriteAsync(schedule, ct);

                logger.LogInformation(
                    "Dispatched schedule {ScheduleId} for agent {AgentName}",
                    schedule.Id,
                    schedule.Agent.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error dispatching due schedules");
        }
    }

    private DateTime? CalculateNextRun(Schedule schedule)
    {
        if (schedule.CronExpression is null)
        {
            return null;
        }

        return cronValidator.GetNextOccurrence(schedule.CronExpression, DateTime.UtcNow);
    }
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Monitor/ScheduleDispatcher.cs
git commit -m "feat(domain): add ScheduleDispatcher"
```

---

## Task 15: Update IChatMessengerClient with CreateTopicIfNeededAsync

**Files:**
- Modify: `Domain/Contracts/IChatMessengerClient.cs`

**Step 1: Add CreateTopicIfNeededAsync method**

Add this method to the interface:

```csharp
Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? userId,
    string? botTokenHash,
    CancellationToken ct = default);
```

The complete interface:

```csharp
using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Domain.Contracts;

public interface IChatMessengerClient
{
    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken);

    Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates, CancellationToken cancellationToken);

    Task<int> CreateThread(long chatId, string name, string? botTokenHash, CancellationToken cancellationToken);
    Task<bool> DoesThreadExist(long chatId, long threadId, string? botTokenHash, CancellationToken cancellationToken);

    Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? userId,
        string? botTokenHash,
        CancellationToken ct = default);
}
```

**Step 2: Verify build fails (implementations missing)**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build fails - implementations don't have CreateTopicIfNeededAsync

**Step 3: Commit interface change**

```bash
git add Domain/Contracts/IChatMessengerClient.cs
git commit -m "feat(domain): add CreateTopicIfNeededAsync to IChatMessengerClient"
```

---

## Task 16: Implement CreateTopicIfNeededAsync in TelegramChatClient

**Files:**
- Modify: `Infrastructure/Clients/Messaging/TelegramChatClient.cs`

**Step 1: Read current implementation to understand structure**

First read the file to understand its structure.

**Step 2: Add CreateTopicIfNeededAsync implementation**

Add this method to `TelegramChatClient`:

```csharp
public async Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? userId,
    string? botTokenHash,
    CancellationToken ct = default)
{
    if (!chatId.HasValue)
    {
        throw new ArgumentException("chatId is required for Telegram", nameof(chatId));
    }

    if (threadId.HasValue)
    {
        var exists = await DoesThreadExist(chatId.Value, threadId.Value, botTokenHash, ct);
        if (exists)
        {
            return new AgentKey(chatId.Value, threadId.Value, botTokenHash);
        }
    }

    var newThreadId = await CreateThread(chatId.Value, "Scheduled task", botTokenHash, ct);
    return new AgentKey(chatId.Value, newThreadId, botTokenHash);
}
```

**Step 3: Verify build**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build may still fail due to other implementations

**Step 4: Commit**

```bash
git add Infrastructure/Clients/Messaging/TelegramChatClient.cs
git commit -m "feat(infrastructure): implement CreateTopicIfNeededAsync in TelegramChatClient"
```

---

## Task 17: Implement CreateTopicIfNeededAsync in WebChatMessengerClient

**Files:**
- Modify: `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`

**Step 1: Read current implementation**

First read the file to understand its structure.

**Step 2: Add CreateTopicIfNeededAsync implementation**

Add this method:

```csharp
public async Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? userId,
    string? botTokenHash,
    CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(userId))
    {
        throw new ArgumentException("userId is required for WebChat", nameof(userId));
    }

    if (threadId.HasValue && chatId.HasValue)
    {
        var exists = await DoesThreadExist(chatId.Value, threadId.Value, botTokenHash, ct);
        if (exists)
        {
            return new AgentKey(chatId.Value, threadId.Value, botTokenHash);
        }
    }

    var newChatId = chatId ?? GenerateChatId();
    var newThreadId = await CreateThread(newChatId, "Scheduled task", botTokenHash, ct);
    return new AgentKey(newChatId, newThreadId, botTokenHash);
}

private static long GenerateChatId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
```

**Step 3: Verify build**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build may still fail due to other implementations

**Step 4: Commit**

```bash
git add Infrastructure/Clients/Messaging/WebChatMessengerClient.cs
git commit -m "feat(infrastructure): implement CreateTopicIfNeededAsync in WebChatMessengerClient"
```

---

## Task 18: Implement CreateTopicIfNeededAsync in Remaining Messenger Clients

**Files:**
- Modify: `Infrastructure/Clients/Messaging/CliChatMessengerClient.cs`
- Modify: `Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs`

**Step 1: Read both files to understand structure**

**Step 2: Add stub implementations**

For `CliChatMessengerClient`:
```csharp
public Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? userId,
    string? botTokenHash,
    CancellationToken ct = default)
{
    return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, botTokenHash));
}
```

For `OneShotChatMessengerClient`:
```csharp
public Task<AgentKey> CreateTopicIfNeededAsync(
    long? chatId,
    long? threadId,
    string? userId,
    string? botTokenHash,
    CancellationToken ct = default)
{
    return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, botTokenHash));
}
```

**Step 3: Verify build succeeds**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add Infrastructure/Clients/Messaging/CliChatMessengerClient.cs Infrastructure/Clients/Messaging/OneShotChatMessengerClient.cs
git commit -m "feat(infrastructure): add CreateTopicIfNeededAsync stubs to CLI and OneShot clients"
```

---

## Task 19: Create ScheduleExecutor

**Files:**
- Create: `Domain/Monitor/ScheduleExecutor.cs`

**Step 1: Create the executor**

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ScheduleExecutor(
    IScheduleStore store,
    IScheduleAgentFactory agentFactory,
    IChatMessengerClient messengerClient,
    Channel<Schedule> scheduleChannel,
    ILogger<ScheduleExecutor> logger)
{
    public async Task ProcessSchedulesAsync(CancellationToken ct)
    {
        var responses = scheduleChannel.Reader
            .ReadAllAsync(ct)
            .Select(schedule => ProcessScheduleAsync(schedule, ct))
            .Merge(ct);

        await messengerClient.ProcessResponseStreamAsync(responses, ct);
    }

    private async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> ProcessScheduleAsync(
        Schedule schedule,
        [EnumeratorCancellation] CancellationToken ct)
    {
        AgentKey? agentKey = null;

        try
        {
            agentKey = await messengerClient.CreateTopicIfNeededAsync(
                schedule.Target.ChatId,
                schedule.Target.ThreadId,
                schedule.Target.UserId,
                schedule.Target.BotTokenHash,
                ct);

            logger.LogInformation(
                "Executing schedule {ScheduleId} for agent {AgentName} on thread {ThreadId}",
                schedule.Id,
                schedule.Agent.Name,
                agentKey.ThreadId);

            await using var agent = agentFactory.CreateFromDefinition(
                agentKey,
                schedule.Target.UserId ?? "scheduler",
                schedule.Agent);

            var thread = await agent.DeserializeThreadAsync(
                JsonSerializer.SerializeToElement(agentKey.ToString()),
                null,
                ct);

            var userMessage = new ChatMessage(ChatRole.User, schedule.Prompt);

            await foreach (var (update, aiResponse) in agent
                .RunStreamingAsync([userMessage], thread, cancellationToken: ct)
                .WithErrorHandling(ct)
                .ToUpdateAiResponsePairs())
            {
                yield return (agentKey, update, aiResponse);
            }

            yield return (agentKey, new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null);

            if (schedule.CronExpression is null)
            {
                await store.DeleteAsync(schedule.Id, ct);
                logger.LogInformation("Deleted one-shot schedule {ScheduleId}", schedule.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error executing schedule {ScheduleId}", schedule.Id);

            if (agentKey is not null)
            {
                yield return (agentKey, new AgentResponseUpdate
                {
                    Contents = [new TextContent($"Schedule execution failed: {ex.Message}")]
                }, null);
                yield return (agentKey, new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null);
            }
        }
    }
}
```

**Step 2: Verify build fails (IScheduleAgentFactory doesn't exist)**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build fails - IScheduleAgentFactory not found

**Step 3: Commit (will fix in next task)**

```bash
git add Domain/Monitor/ScheduleExecutor.cs
git commit -m "feat(domain): add ScheduleExecutor (WIP - needs IScheduleAgentFactory)"
```

---

## Task 20: Create IScheduleAgentFactory Interface

**Files:**
- Create: `Domain/Contracts/IScheduleAgentFactory.cs`

**Step 1: Create the interface**

```csharp
using Domain.Agents;
using Domain.DTOs;

namespace Domain.Contracts;

public interface IScheduleAgentFactory
{
    DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition);
}
```

**Step 2: Verify Domain build succeeds**

Run: `dotnet build G:\repos\agent\Domain\Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Contracts/IScheduleAgentFactory.cs
git commit -m "feat(domain): add IScheduleAgentFactory interface"
```

---

## Task 21: Implement IScheduleAgentFactory in MultiAgentFactory

**Files:**
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`

**Step 1: Add IScheduleAgentFactory to class declaration**

Change:
```csharp
public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    OpenRouterConfig openRouterConfig) : IAgentFactory
```

To:
```csharp
public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    OpenRouterConfig openRouterConfig) : IAgentFactory, IScheduleAgentFactory
```

**Step 2: Verify build succeeds**

Run: `dotnet build G:\repos\agent\Infrastructure\Infrastructure.csproj`
Expected: Build succeeded (CreateFromDefinition already exists)

**Step 3: Commit**

```bash
git add Infrastructure/Agents/MultiAgentFactory.cs
git commit -m "feat(infrastructure): implement IScheduleAgentFactory in MultiAgentFactory"
```

---

## Task 22: Create ScheduleMonitoring BackgroundService

**Files:**
- Create: `Agent/App/ScheduleMonitoring.cs`

**Step 1: Create the background service**

```csharp
using System.Threading.Channels;
using Domain.DTOs;
using Domain.Monitor;

namespace Agent.App;

public class ScheduleMonitoring(
    ScheduleDispatcher dispatcher,
    ScheduleExecutor executor,
    Channel<Schedule> scheduleChannel) : BackgroundService
{
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var dispatchTask = RunDispatcherAsync(ct);
        var executeTask = executor.ProcessSchedulesAsync(ct);

        await Task.WhenAll(dispatchTask, executeTask);
    }

    private async Task RunDispatcherAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await dispatcher.DispatchDueSchedulesAsync(ct);
                await Task.Delay(DispatchInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        finally
        {
            scheduleChannel.Writer.Complete();
        }
    }
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Agent\Agent.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Agent/App/ScheduleMonitoring.cs
git commit -m "feat(agent): add ScheduleMonitoring BackgroundService"
```

---

## Task 23: Create SchedulingModule for DI

**Files:**
- Create: `Agent/Modules/SchedulingModule.cs`

**Step 1: Create the module**

```csharp
using System.Threading.Channels;
using Agent.App;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Domain.Tools.Scheduling;
using Infrastructure.Agents;
using Infrastructure.StateManagers;
using Infrastructure.Validation;

namespace Agent.Modules;

public static class SchedulingModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddScheduling()
        {
            services.AddSingleton(Channel.CreateUnbounded<Schedule>(
                new UnboundedChannelOptions { SingleReader = true }));

            services.AddSingleton<IScheduleStore, RedisScheduleStore>();
            services.AddSingleton<ICronValidator, CronValidator>();
            services.AddSingleton<IAgentDefinitionProvider, AgentDefinitionProvider>();

            services.AddTransient<ScheduleCreateTool>();
            services.AddTransient<ScheduleListTool>();
            services.AddTransient<ScheduleDeleteTool>();

            services.AddSingleton<ScheduleDispatcher>();
            services.AddSingleton<ScheduleExecutor>();

            services.AddHostedService<ScheduleMonitoring>();

            return services;
        }
    }
}
```

**Step 2: Verify build**

Run: `dotnet build G:\repos\agent\Agent\Agent.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Agent/Modules/SchedulingModule.cs
git commit -m "feat(agent): add SchedulingModule for DI configuration"
```

---

## Task 24: Wire Up Scheduling in InjectorModule

**Files:**
- Modify: `Agent/Modules/InjectorModule.cs`

**Step 1: Add IScheduleAgentFactory registration**

In the `AddAgent` method, after creating `MultiAgentFactory`, also register it as `IScheduleAgentFactory`:

```csharp
public IServiceCollection AddAgent(AgentSettings settings)
{
    var llmConfig = new OpenRouterConfig
    {
        ApiUrl = settings.OpenRouter.ApiUrl,
        ApiKey = settings.OpenRouter.ApiKey
    };

    services.Configure<AgentRegistryOptions>(options => options.Agents = settings.Agents);

    return services
        .AddRedis(settings.Redis)
        .AddSingleton<ChatThreadResolver>()
        .AddSingleton<IAgentFactory>(sp =>
            new MultiAgentFactory(
                sp,
                sp.GetRequiredService<IOptionsMonitor<AgentRegistryOptions>>(),
                llmConfig))
        .AddSingleton<IScheduleAgentFactory>(sp =>
            (IScheduleAgentFactory)sp.GetRequiredService<IAgentFactory>());
}
```

**Step 2: Add using statement if needed**

Add at top of file:
```csharp
using Domain.Contracts;
```

**Step 3: Verify build**

Run: `dotnet build G:\repos\agent\Agent\Agent.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add Agent/Modules/InjectorModule.cs
git commit -m "feat(agent): register IScheduleAgentFactory in InjectorModule"
```

---

## Task 25: Add AddScheduling Call to Program.cs

**Files:**
- Modify: `Agent/Program.cs`

**Step 1: Read Program.cs to understand structure**

**Step 2: Add AddScheduling() call**

Find where other modules are added (likely near `AddAgent` or `AddChatMonitoring`) and add:

```csharp
services.AddScheduling();
```

**Step 3: Verify build**

Run: `dotnet build G:\repos\agent\Agent\Agent.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add Agent/Program.cs
git commit -m "feat(agent): enable scheduling in Program.cs"
```

---

## Task 26: Write Unit Tests for CronValidator

**Files:**
- Create: `Tests/Unit/Infrastructure/CronValidatorTests.cs`

**Step 1: Create the test class**

```csharp
using Infrastructure.Validation;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class CronValidatorTests
{
    private readonly CronValidator _validator = new();

    [Theory]
    [InlineData("0 9 * * *", true)]       // Every day at 9am
    [InlineData("*/15 * * * *", true)]    // Every 15 minutes
    [InlineData("0 0 1 * *", true)]       // First of month
    [InlineData("invalid", false)]
    [InlineData("", false)]
    [InlineData("0 9 * *", false)]        // Missing field
    public void IsValid_VariousExpressions_ReturnsExpected(string cron, bool expected)
    {
        _validator.IsValid(cron).ShouldBe(expected);
    }

    [Fact]
    public void GetNextOccurrence_ValidCron_ReturnsNextTime()
    {
        var from = new DateTime(2024, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        var next = _validator.GetNextOccurrence("0 9 * * *", from);

        next.ShouldNotBeNull();
        next.Value.Hour.ShouldBe(9);
        next.Value.Minute.ShouldBe(0);
    }

    [Fact]
    public void GetNextOccurrence_InvalidCron_ReturnsNull()
    {
        var next = _validator.GetNextOccurrence("invalid", DateTime.UtcNow);
        next.ShouldBeNull();
    }
}
```

**Step 2: Run tests**

Run: `dotnet test G:\repos\agent\Tests\Tests.csproj --filter "FullyQualifiedName~CronValidatorTests"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add Tests/Unit/Infrastructure/CronValidatorTests.cs
git commit -m "test: add CronValidator unit tests"
```

---

## Task 27: Write Unit Tests for ScheduleCreateTool

**Files:**
- Create: `Tests/Unit/Domain/Scheduling/ScheduleCreateToolTests.cs`

**Step 1: Create the test class**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Scheduling;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleCreateToolTests
{
    private readonly Mock<IScheduleStore> _store = new();
    private readonly Mock<ICronValidator> _cronValidator = new();
    private readonly Mock<IAgentDefinitionProvider> _agentProvider = new();
    private readonly TestableScheduleCreateTool _tool;

    public ScheduleCreateToolTests()
    {
        _tool = new TestableScheduleCreateTool(_store.Object, _cronValidator.Object, _agentProvider.Object);
    }

    [Fact]
    public async Task Run_MissingAgentId_ReturnsError()
    {
        var result = await _tool.TestRun("", "prompt", "0 9 * * *", null, "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("agentId is required");
    }

    [Fact]
    public async Task Run_NeitherCronNorRunAt_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", null, null, "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("Either cronExpression or runAt must be provided");
    }

    [Fact]
    public async Task Run_BothCronAndRunAt_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", "0 9 * * *", DateTime.UtcNow.AddDays(1), "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("Provide only cronExpression OR runAt, not both");
    }

    [Fact]
    public async Task Run_InvalidCron_ReturnsError()
    {
        _cronValidator.Setup(v => v.IsValid("invalid")).Returns(false);

        var result = await _tool.TestRun("jack", "prompt", "invalid", null, "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldContain("Invalid cron expression");
    }

    [Fact]
    public async Task Run_RunAtInPast_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", null, DateTime.UtcNow.AddHours(-1), "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("runAt must be in the future");
    }

    [Fact]
    public async Task Run_InvalidChannel_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", null, DateTime.UtcNow.AddDays(1), "invalid", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("channel must be 'telegram' or 'webchat'");
    }

    [Fact]
    public async Task Run_AgentNotFound_ReturnsError()
    {
        _agentProvider.Setup(p => p.GetById("unknown")).Returns((AgentDefinition?)null);

        var result = await _tool.TestRun("unknown", "prompt", null, DateTime.UtcNow.AddDays(1), "telegram", 123, null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("Agent 'unknown' not found");
    }

    [Fact]
    public async Task Run_ValidOneShot_CreatesSchedule()
    {
        var agent = new AgentDefinition
        {
            Id = "jack",
            Name = "Jack",
            Model = "test",
            McpServerEndpoints = []
        };
        _agentProvider.Setup(p => p.GetById("jack")).Returns(agent);
        _store.Setup(s => s.CreateAsync(It.IsAny<Schedule>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Schedule s, CancellationToken _) => s);

        var runAt = DateTime.UtcNow.AddDays(1);
        var result = await _tool.TestRun("jack", "test prompt", null, runAt, "telegram", 123, null, null, null);

        result["status"]?.GetValue<string>().ShouldBe("created");
        result["agentName"]?.GetValue<string>().ShouldBe("Jack");
        _store.Verify(s => s.CreateAsync(It.Is<Schedule>(sch =>
            sch.Agent.Id == "jack" &&
            sch.Prompt == "test prompt" &&
            sch.RunAt == runAt), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ValidCron_CreatesSchedule()
    {
        var agent = new AgentDefinition
        {
            Id = "jack",
            Name = "Jack",
            Model = "test",
            McpServerEndpoints = []
        };
        var nextRun = DateTime.UtcNow.AddHours(1);
        _agentProvider.Setup(p => p.GetById("jack")).Returns(agent);
        _cronValidator.Setup(v => v.IsValid("0 9 * * *")).Returns(true);
        _cronValidator.Setup(v => v.GetNextOccurrence("0 9 * * *", It.IsAny<DateTime>())).Returns(nextRun);
        _store.Setup(s => s.CreateAsync(It.IsAny<Schedule>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Schedule s, CancellationToken _) => s);

        var result = await _tool.TestRun("jack", "test prompt", "0 9 * * *", null, "webchat", null, null, "user1", null);

        result["status"]?.GetValue<string>().ShouldBe("created");
        _store.Verify(s => s.CreateAsync(It.Is<Schedule>(sch =>
            sch.CronExpression == "0 9 * * *" &&
            sch.NextRunAt == nextRun), It.IsAny<CancellationToken>()), Times.Once);
    }

    private class TestableScheduleCreateTool(
        IScheduleStore store,
        ICronValidator cronValidator,
        IAgentDefinitionProvider agentProvider)
        : ScheduleCreateTool(store, cronValidator, agentProvider)
    {
        public Task<JsonNode> TestRun(
            string agentId,
            string prompt,
            string? cronExpression,
            DateTime? runAt,
            string channel,
            long? chatId,
            long? threadId,
            string? userId,
            string? botTokenHash)
        {
            return Run(agentId, prompt, cronExpression, runAt, channel, chatId, threadId, userId, botTokenHash);
        }
    }
}
```

**Step 2: Run tests**

Run: `dotnet test G:\repos\agent\Tests\Tests.csproj --filter "FullyQualifiedName~ScheduleCreateToolTests"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add Tests/Unit/Domain/Scheduling/ScheduleCreateToolTests.cs
git commit -m "test: add ScheduleCreateTool unit tests"
```

---

## Task 28: Write Integration Tests for RedisScheduleStore

**Files:**
- Create: `Tests/Integration/StateManagers/RedisScheduleStoreTests.cs`

**Step 1: Create the test class**

```csharp
using Domain.DTOs;
using Infrastructure.StateManagers;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public class RedisScheduleStoreTests : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly RedisScheduleStore _store;
    private readonly IDatabase _db;
    private readonly List<string> _createdIds = [];

    public RedisScheduleStoreTests(RedisFixture fixture)
    {
        _store = new RedisScheduleStore(fixture.Redis);
        _db = fixture.Redis.GetDatabase();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var id in _createdIds)
        {
            await _store.DeleteAsync(id);
        }
    }

    [Fact]
    public async Task CreateAsync_StoresSchedule()
    {
        var schedule = CreateTestSchedule();
        _createdIds.Add(schedule.Id);

        var result = await _store.CreateAsync(schedule);

        result.Id.ShouldBe(schedule.Id);
        var stored = await _store.GetAsync(schedule.Id);
        stored.ShouldNotBeNull();
        stored.Prompt.ShouldBe(schedule.Prompt);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSchedules()
    {
        var schedule1 = CreateTestSchedule();
        var schedule2 = CreateTestSchedule();
        _createdIds.Add(schedule1.Id);
        _createdIds.Add(schedule2.Id);

        await _store.CreateAsync(schedule1);
        await _store.CreateAsync(schedule2);

        var list = await _store.ListAsync();

        list.ShouldContain(s => s.Id == schedule1.Id);
        list.ShouldContain(s => s.Id == schedule2.Id);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSchedule()
    {
        var schedule = CreateTestSchedule();
        await _store.CreateAsync(schedule);

        await _store.DeleteAsync(schedule.Id);

        var stored = await _store.GetAsync(schedule.Id);
        stored.ShouldBeNull();
    }

    [Fact]
    public async Task GetDueSchedulesAsync_ReturnsDueOnly()
    {
        var dueSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        var futureSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddHours(1) };
        _createdIds.Add(dueSchedule.Id);
        _createdIds.Add(futureSchedule.Id);

        await _store.CreateAsync(dueSchedule);
        await _store.CreateAsync(futureSchedule);

        var due = await _store.GetDueSchedulesAsync(DateTime.UtcNow);

        due.ShouldContain(s => s.Id == dueSchedule.Id);
        due.ShouldNotContain(s => s.Id == futureSchedule.Id);
    }

    [Fact]
    public async Task UpdateLastRunAsync_UpdatesSchedule()
    {
        var schedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        _createdIds.Add(schedule.Id);
        await _store.CreateAsync(schedule);

        var newNextRun = DateTime.UtcNow.AddHours(1);
        await _store.UpdateLastRunAsync(schedule.Id, DateTime.UtcNow, newNextRun);

        var updated = await _store.GetAsync(schedule.Id);
        updated.ShouldNotBeNull();
        updated.LastRunAt.ShouldNotBeNull();
        updated.NextRunAt.ShouldBe(newNextRun);
    }

    private static Schedule CreateTestSchedule()
    {
        return new Schedule
        {
            Id = $"test_{Guid.NewGuid():N}",
            Agent = new AgentDefinition
            {
                Id = "test",
                Name = "Test Agent",
                Model = "test-model",
                McpServerEndpoints = []
            },
            Prompt = "Test prompt",
            CronExpression = "0 9 * * *",
            Target = new ScheduleTarget
            {
                Channel = "telegram",
                ChatId = 12345
            },
            CreatedAt = DateTime.UtcNow,
            NextRunAt = DateTime.UtcNow.AddHours(1)
        };
    }
}
```

**Step 2: Run tests (requires Redis)**

Run: `dotnet test G:\repos\agent\Tests\Tests.csproj --filter "FullyQualifiedName~RedisScheduleStoreTests"`
Expected: All tests pass (or skip if Redis not available)

**Step 3: Commit**

```bash
git add Tests/Integration/StateManagers/RedisScheduleStoreTests.cs
git commit -m "test: add RedisScheduleStore integration tests"
```

---

## Task 29: Final Build and Verification

**Step 1: Build entire solution**

Run: `dotnet build G:\repos\agent\Agent.sln`
Expected: Build succeeded

**Step 2: Run all tests**

Run: `dotnet test G:\repos\agent\Tests\Tests.csproj`
Expected: All tests pass

**Step 3: Create summary commit**

```bash
git add -A
git commit -m "feat: complete scheduled agent spawning feature

- Add Schedule DTOs and IScheduleStore interface
- Add ICronValidator and IAgentDefinitionProvider interfaces
- Implement RedisScheduleStore for persistence
- Implement CronValidator using NCrontab
- Implement AgentDefinitionProvider
- Add ScheduleCreateTool, ScheduleListTool, ScheduleDeleteTool
- Add ScheduleDispatcher and ScheduleExecutor with channel-based decoupling
- Add ScheduleMonitoring BackgroundService
- Add CreateTopicIfNeededAsync to IChatMessengerClient
- Add unit and integration tests

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## File Summary

### New Files (14)
| File | Purpose |
|------|---------|
| `Domain/DTOs/Schedule.cs` | Schedule, ScheduleTarget, ScheduleSummary records |
| `Domain/Contracts/IScheduleStore.cs` | Storage interface |
| `Domain/Contracts/ICronValidator.cs` | Cron validation interface |
| `Domain/Contracts/IAgentDefinitionProvider.cs` | Agent lookup interface |
| `Domain/Contracts/IScheduleAgentFactory.cs` | Factory interface for scheduled agents |
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

### Modified Files (7)
| File | Change |
|------|--------|
| `Domain/Domain.csproj` | Add NCrontab package |
| `Domain/Contracts/IThreadStateStore.cs` | Add ExistsAsync method |
| `Domain/Contracts/IChatMessengerClient.cs` | Add CreateTopicIfNeededAsync method |
| `Infrastructure/StateManagers/RedisThreadStateStore.cs` | Implement ExistsAsync |
| `Infrastructure/Agents/MultiAgentFactory.cs` | Implement IScheduleAgentFactory |
| `Infrastructure/Clients/Messaging/*.cs` | Implement CreateTopicIfNeededAsync (4 files) |
| `Agent/Modules/InjectorModule.cs` | Register IScheduleAgentFactory |
| `Agent/Program.cs` | Add AddScheduling() call |

### Test Files (3)
| File | Purpose |
|------|---------|
| `Tests/Unit/Infrastructure/CronValidatorTests.cs` | CronValidator unit tests |
| `Tests/Unit/Domain/Scheduling/ScheduleCreateToolTests.cs` | ScheduleCreateTool unit tests |
| `Tests/Integration/StateManagers/RedisScheduleStoreTests.cs` | RedisScheduleStore integration tests |
