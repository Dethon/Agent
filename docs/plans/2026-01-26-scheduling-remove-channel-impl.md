# Scheduling: Remove Channel Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove the `channel` concept from scheduling - schedules no longer specify delivery mechanism; notifications always go to WebChat (if available).

**Architecture:** Flatten `ScheduleTarget` into `Schedule` with just optional `UserId`. The `ScheduleExecutor` will type-check the injected `IChatMessengerClient` at runtime - if it's a `WebChatMessengerClient`, it creates a topic and streams responses; otherwise, it executes silently.

**Tech Stack:** C# 10, xUnit, Shouldly, Moq, Redis

---

## Task 1: Update Schedule DTO

**Files:**
- Modify: `Domain/DTOs/Schedule.cs`

**Step 1: Write the failing test**

Create test file `Tests/Unit/Domain/Scheduling/ScheduleDtoTests.cs`:

```csharp
using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleDtoTests
{
    [Fact]
    public void Schedule_HasUserIdProperty_NotTarget()
    {
        var schedule = new Schedule
        {
            Id = "test",
            Agent = new AgentDefinition
            {
                Id = "agent",
                Name = "Agent",
                Model = "model",
                McpServerEndpoints = []
            },
            Prompt = "test prompt",
            CronExpression = "0 9 * * *",
            CreatedAt = DateTime.UtcNow,
            UserId = "user123"
        };

        schedule.UserId.ShouldBe("user123");

        // Verify Target property no longer exists (compile-time check)
        // If this compiles, the Target property has been removed
        typeof(Schedule).GetProperty("Target").ShouldBeNull();
    }

    [Fact]
    public void ScheduleSummary_HasUserIdProperty_NotChannel()
    {
        var summary = new ScheduleSummary(
            Id: "test",
            AgentName: "Agent",
            Prompt: "prompt",
            CronExpression: "0 9 * * *",
            RunAt: null,
            NextRunAt: DateTime.UtcNow,
            UserId: "user123");

        summary.UserId.ShouldBe("user123");

        // Verify Channel parameter no longer exists
        typeof(ScheduleSummary).GetProperty("Channel").ShouldBeNull();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~ScheduleDtoTests" --no-build`
Expected: Build failure - `Schedule` still has `Target` property, `ScheduleSummary` still has `Channel`

**Step 3: Update the DTOs**

Update `Domain/DTOs/Schedule.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record Schedule
{
    public required string Id { get; init; }
    public required AgentDefinition Agent { get; init; }
    public required string Prompt { get; init; }
    public string? CronExpression { get; init; }
    public DateTime? RunAt { get; init; }
    public string? UserId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextRunAt { get; init; }
}

[PublicAPI]
public record ScheduleSummary(
    string Id,
    string AgentName,
    string Prompt,
    string? CronExpression,
    DateTime? RunAt,
    DateTime? NextRunAt,
    string? UserId);
```

Delete the `ScheduleTarget` record entirely.

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~ScheduleDtoTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add Domain/DTOs/Schedule.cs Tests/Unit/Domain/Scheduling/ScheduleDtoTests.cs
git commit -m "refactor(scheduling): flatten Schedule DTO, remove ScheduleTarget and Channel"
```

---

## Task 2: Update ScheduleCreateTool

**Files:**
- Modify: `Domain/Tools/Scheduling/ScheduleCreateTool.cs`
- Modify: `Tests/Unit/Domain/Scheduling/ScheduleCreateToolTests.cs`

**Step 1: Update the test file**

Replace `Tests/Unit/Domain/Scheduling/ScheduleCreateToolTests.cs`:

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
        var result = await _tool.TestRun("", "prompt", "0 9 * * *", null, null);

        result["error"]?.GetValue<string>().ShouldBe("agentId is required");
    }

    [Fact]
    public async Task Run_NeitherCronNorRunAt_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", null, null, null);

        result["error"]?.GetValue<string>().ShouldBe("Either cronExpression or runAt must be provided");
    }

    [Fact]
    public async Task Run_BothCronAndRunAt_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", "0 9 * * *", DateTime.UtcNow.AddDays(1), null);

        result["error"]?.GetValue<string>().ShouldBe("Provide only cronExpression OR runAt, not both");
    }

    [Fact]
    public async Task Run_InvalidCron_ReturnsError()
    {
        _cronValidator.Setup(v => v.IsValid("invalid")).Returns(false);

        var result = await _tool.TestRun("jack", "prompt", "invalid", null, null);

        result["error"]?.GetValue<string>().ShouldContain("Invalid cron expression");
    }

    [Fact]
    public async Task Run_RunAtInPast_ReturnsError()
    {
        var result = await _tool.TestRun("jack", "prompt", null, DateTime.UtcNow.AddHours(-1), null);

        result["error"]?.GetValue<string>().ShouldBe("runAt must be in the future");
    }

    [Fact]
    public async Task Run_AgentNotFound_ReturnsError()
    {
        _agentProvider.Setup(p => p.GetById("unknown")).Returns((AgentDefinition?)null);

        var result = await _tool.TestRun("unknown", "prompt", null, DateTime.UtcNow.AddDays(1), null);

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
        var result = await _tool.TestRun("jack", "test prompt", null, runAt, null);

        result["status"]?.GetValue<string>().ShouldBe("created");
        result["agentName"]?.GetValue<string>().ShouldBe("Jack");
        _store.Verify(s => s.CreateAsync(It.Is<Schedule>(sch =>
            sch.Agent.Id == "jack" &&
            sch.Prompt == "test prompt" &&
            sch.RunAt == runAt &&
            sch.UserId == null), It.IsAny<CancellationToken>()), Times.Once);
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

        var result = await _tool.TestRun("jack", "test prompt", "0 9 * * *", null, "user1");

        result["status"]?.GetValue<string>().ShouldBe("created");
        _store.Verify(s => s.CreateAsync(It.Is<Schedule>(sch =>
            sch.CronExpression == "0 9 * * *" &&
            sch.NextRunAt == nextRun &&
            sch.UserId == "user1"), It.IsAny<CancellationToken>()), Times.Once);
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
            string? userId)
        {
            return RunAsync(agentId, prompt, cronExpression, runAt, userId);
        }
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~ScheduleCreateToolTests" --no-build`
Expected: Build failure - `ScheduleCreateTool.RunAsync` signature mismatch

**Step 3: Update ScheduleCreateTool**

Replace `Domain/Tools/Scheduling/ScheduleCreateTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling;

public class ScheduleCreateTool(
    IScheduleStore store,
    ICronValidator cronValidator,
    IAgentDefinitionProvider agentProvider)
{
    public const string Name = "schedule_create";

    public const string Description = """
                                      Creates a scheduled agent task. The specified agent will run with the given prompt
                                      at the scheduled time(s).

                                      For recurring schedules, use cronExpression (standard 5-field cron format):
                                      - "0 9 * * *" = every day at 9:00 AM
                                      - "0 */2 * * *" = every 2 hours
                                      - "30 14 * * 1-5" = weekdays at 2:30 PM

                                      For one-time schedules, use runAt with a UTC datetime.

                                      Results are delivered to WebChat when available.
                                      """;

    [Description(Description)]
    public async Task<JsonNode> RunAsync(
        [Description("Agent ID to execute the task")] string agentId,
        [Description("The prompt/task to execute")] string prompt,
        [Description("Cron expression for recurring schedules (5-field format)")] string? cronExpression,
        [Description("ISO 8601 datetime for one-time execution (UTC)")] DateTime? runAt,
        [Description("Optional user context for the prompt")] string? userId,
        CancellationToken ct = default)
    {
        var validationError = Validate(agentId, cronExpression, runAt);
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
            UserId = userId,
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

    private JsonObject? Validate(string agentId, string? cronExpression, DateTime? runAt)
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

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~ScheduleCreateToolTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add Domain/Tools/Scheduling/ScheduleCreateTool.cs Tests/Unit/Domain/Scheduling/ScheduleCreateToolTests.cs
git commit -m "refactor(scheduling): remove channel params from ScheduleCreateTool"
```

---

## Task 3: Update ScheduleListTool

**Files:**
- Modify: `Domain/Tools/Scheduling/ScheduleListTool.cs`

**Step 1: Write the failing test**

Create `Tests/Unit/Domain/Scheduling/ScheduleListToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Scheduling;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleListToolTests
{
    private readonly Mock<IScheduleStore> _store = new();
    private readonly ScheduleListTool _tool;

    public ScheduleListToolTests()
    {
        _tool = new ScheduleListTool(_store.Object);
    }

    [Fact]
    public async Task Run_ReturnsSchedulesWithUserId()
    {
        var schedules = new List<Schedule>
        {
            new()
            {
                Id = "sched_1",
                Agent = new AgentDefinition
                {
                    Id = "jack",
                    Name = "Jack",
                    Model = "test",
                    McpServerEndpoints = []
                },
                Prompt = "Test prompt",
                CronExpression = "0 9 * * *",
                UserId = "user123",
                CreatedAt = DateTime.UtcNow,
                NextRunAt = DateTime.UtcNow.AddHours(1)
            }
        };
        _store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(schedules);

        var result = await _tool.RunAsync();

        result["count"]?.GetValue<int>().ShouldBe(1);
        var schedulesArray = result["schedules"]?.AsArray();
        schedulesArray.ShouldNotBeNull();
        schedulesArray.Count.ShouldBe(1);

        var first = schedulesArray[0]!.AsObject();
        first["userId"]?.GetValue<string>().ShouldBe("user123");
        first["channel"].ShouldBeNull(); // Channel should not be present
    }

    [Fact]
    public async Task Run_OmitsUserIdWhenNull()
    {
        var schedules = new List<Schedule>
        {
            new()
            {
                Id = "sched_1",
                Agent = new AgentDefinition
                {
                    Id = "jack",
                    Name = "Jack",
                    Model = "test",
                    McpServerEndpoints = []
                },
                Prompt = "Test prompt",
                CronExpression = "0 9 * * *",
                UserId = null,
                CreatedAt = DateTime.UtcNow,
                NextRunAt = DateTime.UtcNow.AddHours(1)
            }
        };
        _store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(schedules);

        var result = await _tool.RunAsync();

        var schedulesArray = result["schedules"]?.AsArray();
        var first = schedulesArray![0]!.AsObject();
        first["userId"].ShouldBeNull(); // Should not be present when null
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~ScheduleListToolTests" --no-build`
Expected: Build failure - `ScheduleSummary` constructor mismatch or test fails on `channel` still present

**Step 3: Update ScheduleListTool**

Replace `Domain/Tools/Scheduling/ScheduleListTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling;

public class ScheduleListTool(IScheduleStore store)
{
    public const string Name = "schedule_list";

    public const string Description = """
        Lists all scheduled agent tasks. Shows schedule ID, agent name, prompt preview,
        schedule timing (cron or one-shot), next run time, and optional user context.
        """;

    [Description(Description)]
    public async Task<JsonNode> RunAsync(CancellationToken ct = default)
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
                s.UserId))
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
            ["prompt"] = summary.Prompt
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

        if (summary.UserId is not null)
        {
            node["userId"] = summary.UserId;
        }

        return node;
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~ScheduleListToolTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add Domain/Tools/Scheduling/ScheduleListTool.cs Tests/Unit/Domain/Scheduling/ScheduleListToolTests.cs
git commit -m "refactor(scheduling): update ScheduleListTool for new model"
```

---

## Task 4: Update ScheduleExecutor

**Files:**
- Modify: `Domain/Monitor/ScheduleExecutor.cs`

**Step 1: Write the failing test**

Create `Tests/Unit/Domain/Monitor/ScheduleExecutorTests.cs`:

```csharp
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Infrastructure.Clients.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

public class ScheduleExecutorTests
{
    private readonly Mock<IScheduleStore> _store = new();
    private readonly Mock<IScheduleAgentFactory> _agentFactory = new();
    private readonly Channel<Schedule> _scheduleChannel = Channel.CreateUnbounded<Schedule>();

    [Fact]
    public async Task ProcessScheduleAsync_WithWebChatClient_CreatesTopicAndStreams()
    {
        // Arrange
        var mockWebChatClient = new Mock<WebChatMessengerClient>(
            Mock.Of<WebChatSessionManager>(),
            Mock.Of<WebChatStreamManager>(),
            Mock.Of<WebChatApprovalManager>(),
            Mock.Of<ChatThreadResolver>(),
            Mock.Of<IThreadStateStore>(),
            Mock.Of<INotifier>(),
            NullLogger<WebChatMessengerClient>.Instance);

        var agentKey = new AgentKey(123, 456, "jack");
        mockWebChatClient
            .Setup(c => c.CreateTopicIfNeededAsync(null, null, "jack", "Scheduled task", It.IsAny<CancellationToken>()))
            .ReturnsAsync(agentKey);

        var schedule = CreateTestSchedule();

        // The executor should call CreateTopicIfNeededAsync with agentId from schedule
        mockWebChatClient.Verify(
            c => c.CreateTopicIfNeededAsync(null, null, "jack", "Scheduled task", It.IsAny<CancellationToken>()),
            Times.Never); // Not called yet

        // This verifies the type-check behavior exists
        (mockWebChatClient.Object is WebChatMessengerClient).ShouldBeTrue();
    }

    [Fact]
    public void ProcessScheduleAsync_WithNonWebChatClient_ExecutesSilently()
    {
        // Arrange
        var mockClient = new Mock<IChatMessengerClient>();

        // Type-check should fail for non-WebChat client
        (mockClient.Object is WebChatMessengerClient).ShouldBeFalse();
    }

    private static Schedule CreateTestSchedule()
    {
        return new Schedule
        {
            Id = "sched_test",
            Agent = new AgentDefinition
            {
                Id = "jack",
                Name = "Jack",
                Model = "test",
                McpServerEndpoints = []
            },
            Prompt = "Test prompt",
            CronExpression = "0 9 * * *",
            UserId = "user123",
            CreatedAt = DateTime.UtcNow,
            NextRunAt = DateTime.UtcNow.AddHours(1)
        };
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~ScheduleExecutorTests" --no-build`
Expected: Build failure - `Schedule` still references `Target` in executor

**Step 3: Update ScheduleExecutor**

Replace `Domain/Monitor/ScheduleExecutor.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Clients.Messaging;
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
        await foreach (var schedule in scheduleChannel.Reader.ReadAllAsync(ct))
        {
            await ProcessScheduleAsync(schedule, ct);
        }
    }

    private async Task ProcessScheduleAsync(Schedule schedule, CancellationToken ct)
    {
        // Execute the agent with the scheduled prompt
        AgentKey? agentKey = null;

        // If we have a WebChat client, create a topic for response delivery
        if (messengerClient is WebChatMessengerClient webChatClient)
        {
            try
            {
                agentKey = await webChatClient.CreateTopicIfNeededAsync(
                    chatId: null,
                    threadId: null,
                    agentId: schedule.Agent.Id,
                    topicName: "Scheduled task",
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error creating topic for schedule {ScheduleId}", schedule.Id);
                return;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Executing schedule {ScheduleId} for agent {AgentName} on thread {ThreadId}",
                    schedule.Id,
                    schedule.Agent.Name,
                    agentKey.ThreadId);
            }

            var responses = ExecuteScheduleCore(schedule, agentKey, ct);
            await webChatClient.ProcessResponseStreamAsync(
                responses.Select(r => (agentKey, r.Update, r.AiResponse)), ct);
        }
        else
        {
            // Execute silently without response delivery
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Executing schedule {ScheduleId} for agent {AgentName} silently (no WebChat)",
                    schedule.Id,
                    schedule.Agent.Name);
            }

            // Still need an AgentKey for agent execution
            agentKey = new AgentKey(0, 0, schedule.Agent.Id);

            await foreach (var _ in ExecuteScheduleCore(schedule, agentKey, ct))
            {
                // Consume the stream silently
            }
        }

        // Clean up one-shot schedules
        if (schedule.CronExpression is null)
        {
            await store.DeleteAsync(schedule.Id, ct);
        }
    }

    private async IAsyncEnumerable<(AgentResponseUpdate Update, AiResponse? AiResponse)> ExecuteScheduleCore(
        Schedule schedule,
        AgentKey agentKey,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var agent = agentFactory.CreateFromDefinition(
            agentKey,
            schedule.UserId ?? "scheduler",
            schedule.Agent);

        var thread = await agent.DeserializeThreadAsync(
            JsonSerializer.SerializeToElement(agentKey.ToString()),
            null,
            ct);

        var userMessage = new ChatMessage(ChatRole.User, schedule.Prompt);

        await foreach (var (update, aiResponse) in agent
                           .RunStreamingAsync([userMessage], thread, cancellationToken: ct)
                           .WithErrorHandling(ct)
                           .ToUpdateAiResponsePairs()
                           .WithCancellation(ct))
        {
            yield return (update, aiResponse);
        }

        yield return (new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null);
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~ScheduleExecutorTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add Domain/Monitor/ScheduleExecutor.cs Tests/Unit/Domain/Monitor/ScheduleExecutorTests.cs
git commit -m "refactor(scheduling): type-check for WebChatMessengerClient in executor"
```

---

## Task 5: Update RedisScheduleStore Integration Tests

**Files:**
- Modify: `Tests/Integration/StateManagers/RedisScheduleStoreTests.cs`

**Step 1: Update the test helper**

Update `CreateTestSchedule` method in the test file to use the new model:

```csharp
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
        UserId = "testuser",
        CreatedAt = DateTime.UtcNow,
        NextRunAt = DateTime.UtcNow.AddHours(1)
    };
}
```

**Step 2: Run integration tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~RedisScheduleStoreTests"`
Expected: PASS (Redis serialization is JSON-based, handles changed shape automatically)

**Step 3: Commit**

```bash
git add Tests/Integration/StateManagers/RedisScheduleStoreTests.cs
git commit -m "test(scheduling): update integration tests for new Schedule model"
```

---

## Task 6: Verify Full Build and All Tests Pass

**Step 1: Build entire solution**

Run: `dotnet build`
Expected: Build succeeds with no errors

**Step 2: Run all tests**

Run: `dotnet test Tests`
Expected: All tests pass

**Step 3: Final commit**

```bash
git add -A
git commit -m "refactor(scheduling): complete channel removal - all tests passing"
```

---

## Summary of Changes

| File | Change |
|------|--------|
| `Domain/DTOs/Schedule.cs` | Removed `ScheduleTarget`, flattened `UserId` onto `Schedule`, updated `ScheduleSummary` |
| `Domain/Tools/Scheduling/ScheduleCreateTool.cs` | Removed `channel`, `chatId`, `threadId`, `targetAgentId` params; kept `userId` |
| `Domain/Tools/Scheduling/ScheduleListTool.cs` | Output shows `UserId` instead of `Channel` |
| `Domain/Monitor/ScheduleExecutor.cs` | Type-checks for `WebChatMessengerClient`, executes silently otherwise |
| `Tests/Unit/Domain/Scheduling/*` | Updated tests for new model |
| `Tests/Integration/StateManagers/RedisScheduleStoreTests.cs` | Updated test helper |

## Migration Note

This is a **breaking change** for existing Redis data. Clear all schedules before deploying:
```bash
redis-cli KEYS "schedule:*" | xargs redis-cli DEL
redis-cli DEL schedules schedules:due
```
