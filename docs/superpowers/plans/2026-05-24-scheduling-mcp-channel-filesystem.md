# Scheduling as an MCP Server (Channel + Filesystem) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the in-process scheduling feature with a dedicated `McpServerScheduling` that fires due schedules as inbound `channel/message` notifications (collapsing the duplicated execution loop into `ChatMonitor`) and exposes a `filesystem://schedules` control surface managed with the existing VFS verbs.

**Architecture:** One MCP server process in two roles over a single `/mcp` endpoint. Channel role: a `BackgroundService` cron loop emits `notifications/channel/message` carrying the prompt, target `agentId`, a multi-target `ReplyTo` list, and a schedule `Origin`. Filesystem role: a synthetic VFS over `IScheduleStore` exposes schedules grouped by agent (`/schedules/<agentId>/<scheduleId>/...`). The agent hub (`ChatMonitor`) fans replies out to every `ReplyTo` target and re-emits `ScheduleExecutionEvent` for schedule-origin runs.

**Tech Stack:** .NET 10, C# 14, `ModelContextProtocol.AspNetCore` 1.2.0, StackExchange.Redis, NCrontab, xUnit.

**Spec:** `docs/superpowers/specs/2026-05-24-scheduling-mcp-channel-filesystem-design.md`

---

## Conventions (read before every task)

- **TDD is mandatory.** Write the failing test, **run it and paste the RED failure output**, then implement, then run GREEN. A GREEN claim without prior RED output is a process failure (project rule).
- **No trailing newline** in any `.cs` file, including tests (project rule — verified across the repo).
- **Modern C#:** file-scoped namespaces, primary constructors, `record` DTOs, LINQ over loops, `CancellationToken` on async paths, no XML doc comments.
- **MCP tool wrappers carry no try/catch** — error handling is centralized in each server's `ConfigModule` via `AddCallToolFilter` (see `.claude/rules/mcp-tools.md`).
- **`fs_*` success payloads are strict:** they must deserialize under `FsResultContract.ValidationOptions` (camelCase, `UnmappedMemberHandling.Disallow`). Reuse the existing DTOs in `Domain/DTOs/FileSystem/`; do not invent fields.
- **Build/test commands:**
  - Build one project: `dotnet build McpServerScheduling/McpServerScheduling.csproj`
  - Run a unit test: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<TestClass>"`
  - The repo has a formatting hook; keep single-line `if` braces as the hook expects.
- **Pre-existing baseline:** ~148 `Category!=E2E` tests fail in this WSL env due to `DockerUnavailableException` (Redis/containers). Those are not regressions. Run targeted `--filter` for the tests you add.

---

## File Structure

**New project `McpServerScheduling/`** (mirrors `McpChannelServiceBus` + `McpServerVault`):
- `McpServerScheduling.csproj` — references `Infrastructure` (for `RedisScheduleStore`, `CronValidator`) and `Domain`.
- `Program.cs` — host bootstrap, `app.MapMcp("/mcp")`.
- `Modules/ConfigModule.cs` — `GetSettings()` + `ConfigureScheduling()` DI (Redis, store, cron, emitter, dispatcher, MCP tools + resource + call-tool error filter).
- `Settings/SchedulingSettings.cs` — Redis connection, default `DeliverTo`, dispatch interval, agent registry list.
- `Services/ScheduleNotificationEmitter.cs` — emits `notifications/channel/message` (with `ReplyTo`/`Origin`).
- `Services/ScheduleDispatcherService.cs` — `BackgroundService` cron loop.
- `McpResources/FileSystemResource.cs` — publishes `filesystem://schedules`.
- `McpTools/Fs{Glob,Read,Info,Search,Create,Edit,Delete,Move,Exec}Tool.cs` — thin wrappers over the engine.
- `McpTools/SendReplyTool.cs`, `McpTools/RequestApprovalTool.cs` — channel outbound (auto-approve).
- `Dockerfile`, `appsettings.json`.

**New Domain code** (engine has no Infrastructure deps — depends only on contracts):
- `Domain/DTOs/Channel/ReplyTarget.cs`, `Domain/DTOs/Channel/MessageOrigin.cs` — channel routing DTOs.
- `Domain/Contracts/IScheduleAgentCatalog.cs` — minimal agent list (id/name/description) for the server.
- `Domain/DTOs/ScheduleAgentInfo.cs` — agent metadata DTO.
- `Domain/Tools/Scheduling/Vfs/SchedulePath.cs` — pure path → node resolver.
- `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs` — the VFS engine (glob/read/info/search/create/edit/delete/move/exec) over `IScheduleStore` + `IScheduleAgentCatalog` + `ICronValidator`.

**Modified:**
- `Domain/DTOs/ChannelMessage.cs` — add `ReplyTo`, `Origin`.
- `Domain/DTOs/Schedule.cs` — `Agent` (AgentDefinition) → `AgentId` (string); add `DeliverTo`; remove `ScheduleSummary`.
- `Infrastructure/Clients/Channels/McpChannelConnection.cs` — parse `replyTo`/`origin`.
- `Domain/Monitor/ChatMonitor.cs` — multi-target fan-out + schedule-origin metric emission.
- `Agent/Modules/InjectorModule.cs` — drop `IScheduleAgentFactory` registration.
- `Agent/appsettings.json` / `DockerCompose/docker-compose.yml` — add the new server.

**Deleted:** `Domain/Monitor/ScheduleExecutor.cs`, `Domain/Monitor/ScheduleDispatcher.cs`, `Agent/App/ScheduleMonitoring.cs`, `Domain/Contracts/IScheduleAgentFactory.cs`, `Agent/Modules/SchedulingModule.cs`, `Domain/Tools/Scheduling/{ScheduleCreateTool,ScheduleListTool,ScheduleDeleteTool,SchedulingToolFeature}.cs`.

---

# Phase 1 — Channel contract foundation (agent-side, additive)

No behavior change until a producer sets the new fields; fully unit-testable now.

## Task 1: Channel routing DTOs + ChannelMessage fields

**Files:**
- Create: `Domain/DTOs/Channel/ReplyTarget.cs`
- Create: `Domain/DTOs/Channel/MessageOrigin.cs`
- Modify: `Domain/DTOs/ChannelMessage.cs`
- Test: `Tests/Unit/Domain/DTOs/ChannelMessageTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;
using Xunit;

namespace Tests.Unit.Domain.DTOs;

public class ChannelMessageTests
{
    [Fact]
    public void ChannelMessage_carries_optional_replyto_and_origin()
    {
        var message = new ChannelMessage
        {
            ConversationId = "c1",
            Content = "hi",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-42")],
            Origin = new MessageOrigin("schedule", "morning-news")
        };

        Assert.Equal(2, message.ReplyTo!.Count);
        Assert.Equal("signalr", message.ReplyTo[0].ChannelId);
        Assert.Null(message.ReplyTo[0].ConversationId);
        Assert.Equal("schedule", message.Origin!.Kind);
        Assert.Equal("morning-news", message.Origin.ScheduleId);
    }

    [Fact]
    public void ChannelMessage_replyto_and_origin_default_to_null()
    {
        var message = new ChannelMessage
        {
            ConversationId = "c1",
            Content = "hi",
            Sender = "u",
            ChannelId = "signalr"
        };

        Assert.Null(message.ReplyTo);
        Assert.Null(message.Origin);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelMessageTests"`
Expected: FAIL — compile error, `ReplyTarget`/`MessageOrigin` not found and `ChannelMessage` has no `ReplyTo`/`Origin`.

- [ ] **Step 3: Create the DTOs and extend ChannelMessage**

`Domain/DTOs/Channel/ReplyTarget.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ReplyTarget(string ChannelId, string? ConversationId);
```

`Domain/DTOs/Channel/MessageOrigin.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record MessageOrigin(string Kind, string? ScheduleId);
```

Edit `Domain/DTOs/ChannelMessage.cs` — add the two `using` and two properties:

```csharp
using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ChannelMessage
{
    public required string ConversationId { get; init; }
    public required string Content { get; init; }
    public required string Sender { get; init; }
    public required string ChannelId { get; init; }
    public string? AgentId { get; init; }
    public IReadOnlyList<ReplyTarget>? ReplyTo { get; init; }
    public MessageOrigin? Origin { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelMessageTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Channel/ReplyTarget.cs Domain/DTOs/Channel/MessageOrigin.cs Domain/DTOs/ChannelMessage.cs Tests/Unit/Domain/DTOs/ChannelMessageTests.cs
git commit -m "feat(channel): add ReplyTo/Origin to ChannelMessage"
```

## Task 2: Parse replyTo/origin in McpChannelConnection

**Files:**
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs` (method `HandleChannelMessageNotification`)
- Test: `Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs`

`HandleChannelMessageNotification` is `public`, so it can be tested directly with a crafted `JsonElement`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Infrastructure.Clients.Channels;
using Xunit;

namespace Tests.Unit.Infrastructure.Channels;

public class McpChannelConnectionParsingTests
{
    private static JsonElement Json(string s) => JsonSerializer.Deserialize<JsonElement>(s);

    [Fact]
    public async Task Parses_replyTo_and_origin_when_present()
    {
        var conn = new McpChannelConnection("scheduling");
        conn.HandleChannelMessageNotification(Json("""
        {
          "conversationId": "c1",
          "content": "run it",
          "sender": "scheduler",
          "agentId": "jonas",
          "replyTo": [{"channelId":"signalr","conversationId":null},{"channelId":"telegram","conversationId":"t-1"}],
          "origin": {"kind":"schedule","scheduleId":"morning-news"}
        }
        """));

        var msg = await conn.Messages.FirstAsync();

        Assert.Equal("jonas", msg.AgentId);
        Assert.Equal(2, msg.ReplyTo!.Count);
        Assert.Equal("telegram", msg.ReplyTo[1].ChannelId);
        Assert.Equal("t-1", msg.ReplyTo[1].ConversationId);
        Assert.Equal("schedule", msg.Origin!.Kind);
        Assert.Equal("morning-news", msg.Origin.ScheduleId);
    }

    [Fact]
    public async Task Leaves_replyTo_and_origin_null_when_absent()
    {
        var conn = new McpChannelConnection("signalr");
        conn.HandleChannelMessageNotification(Json("""
        {"conversationId":"c1","content":"hi","sender":"user"}
        """));

        var msg = await conn.Messages.FirstAsync();

        Assert.Null(msg.ReplyTo);
        Assert.Null(msg.Origin);
    }
}
```

> `FirstAsync()` over `IAsyncEnumerable` comes from `System.Linq.Async` already used in the repo (see `Domain/Monitor` LINQ-async usage). If the namespace differs, mirror the existing test helpers under `Tests/Unit/Infrastructure`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelConnectionParsingTests"`
Expected: FAIL — `msg.ReplyTo`/`msg.Origin` are null in the first test (or compile error if asserted before they exist).

- [ ] **Step 3: Extend the parser**

In `Infrastructure/Clients/Channels/McpChannelConnection.cs`, add `using Domain.DTOs.Channel;` and replace the body of `HandleChannelMessageNotification` so it parses the optional structured fields (mirror the existing `agentId` `TryGetProperty` pattern):

```csharp
public void HandleChannelMessageNotification(JsonElement payload)
{
    var conversationId = payload.GetProperty("conversationId").GetString()!;
    var content = payload.GetProperty("content").GetString()!;
    var sender = payload.GetProperty("sender").GetString()!;
    var agentId = payload.TryGetProperty("agentId", out var agentIdProp)
        ? agentIdProp.GetString()
        : null;

    var replyTo = payload.TryGetProperty("replyTo", out var replyToProp)
                  && replyToProp.ValueKind == JsonValueKind.Array
        ? replyToProp.EnumerateArray()
            .Select(t => new ReplyTarget(
                t.GetProperty("channelId").GetString()!,
                t.TryGetProperty("conversationId", out var cid) && cid.ValueKind == JsonValueKind.String
                    ? cid.GetString()
                    : null))
            .ToList()
        : null;

    var origin = payload.TryGetProperty("origin", out var originProp)
                 && originProp.ValueKind == JsonValueKind.Object
        ? new MessageOrigin(
            originProp.GetProperty("kind").GetString()!,
            originProp.TryGetProperty("scheduleId", out var sid) && sid.ValueKind == JsonValueKind.String
                ? sid.GetString()
                : null)
        : null;

    var message = new ChannelMessage
    {
        ConversationId = conversationId,
        Content = content,
        Sender = sender,
        ChannelId = ChannelId,
        AgentId = agentId,
        ReplyTo = replyTo,
        Origin = origin
    };

    _messageChannel.Writer.TryWrite(message);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelConnectionParsingTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Channels/McpChannelConnection.cs Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs
git commit -m "feat(channel): parse replyTo/origin in McpChannelConnection"
```

## Task 3: ChatMonitor multi-target reply fan-out

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs`
- Test: `Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs`

Introduce a target resolver and fan replies to every target. Default (no `ReplyTo`) keeps current behavior. Conversations with a null `ConversationId` are minted once via `CreateConversationAsync`.

- [ ] **Step 1: Write the failing test** (resolver unit — pure logic extracted to a static helper)

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Moq;
using Xunit;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorDeliveryTests
{
    private static IChannelConnection Channel(string id)
    {
        var m = new Mock<IChannelConnection>();
        m.SetupGet(c => c.ChannelId).Returns(id);
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string a, string t, string s, CancellationToken _) => $"minted-{id}");
        return m.Object;
    }

    [Fact]
    public async Task No_replyto_delivers_to_origin()
    {
        var origin = Channel("signalr");
        var channels = new[] { origin, Channel("telegram") };
        var msg = new ChannelMessage { ConversationId = "c1", Content = "x", Sender = "u", ChannelId = "signalr" };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        var t = Assert.Single(targets);
        Assert.Equal("signalr", t.Channel.ChannelId);
        Assert.Equal("c1", t.ConversationId);
    }

    [Fact]
    public async Task Replyto_fans_out_and_mints_missing_conversations()
    {
        var origin = Channel("scheduling");
        var channels = new[] { origin, Channel("signalr"), Channel("telegram") };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "x",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-9")]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        Assert.Equal(2, targets.Count);
        Assert.Equal("minted-signalr", targets[0].ConversationId);
        Assert.Equal("t-9", targets[1].ConversationId);
    }

    [Fact]
    public async Task Unknown_channel_in_replyto_is_skipped()
    {
        var origin = Channel("scheduling");
        var channels = new[] { origin, Channel("signalr") };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1", Content = "x", Sender = "s", ChannelId = "scheduling",
            ReplyTo = [new ReplyTarget("does-not-exist", "z")]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        Assert.Empty(targets);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorDeliveryTests"`
Expected: FAIL — `ChatMonitor.ResolveDeliveryTargetsAsync` and the `DeliveryTarget` type do not exist.

- [ ] **Step 3: Add the resolver and wire it into delivery**

In `Domain/Monitor/ChatMonitor.cs`, add `using Domain.DTOs.Channel;`, a `DeliveryTarget` record, and a static resolver:

```csharp
public readonly record struct DeliveryTarget(IChannelConnection Channel, string ConversationId);

public static async Task<IReadOnlyList<DeliveryTarget>> ResolveDeliveryTargetsAsync(
    ChannelMessage message,
    IChannelConnection originChannel,
    IReadOnlyList<IChannelConnection> channels,
    CancellationToken ct)
{
    if (message.ReplyTo is not { Count: > 0 })
    {
        return [new DeliveryTarget(originChannel, message.ConversationId)];
    }

    var targets = new List<DeliveryTarget>();
    foreach (var target in message.ReplyTo)
    {
        var channel = channels.FirstOrDefault(c => c.ChannelId == target.ChannelId);
        if (channel is null)
        {
            continue;
        }

        var conversationId = target.ConversationId
            ?? await channel.CreateConversationAsync(
                message.AgentId ?? "default", "Scheduled task", message.Sender, ct);

        if (conversationId is not null)
        {
            targets.Add(new DeliveryTarget(channel, conversationId));
        }
    }

    return targets;
}
```

Now thread targets through delivery. In `ProcessChatThread`, the `aiResponses` projection currently ends with
`.Select(pair => (pair.Item1, pair.Item2, x.Channel, x.Message.ConversationId));`. Replace the per-message `default:` branch tail so it resolves targets once and tags each update with them:

```csharp
default:
    var userMessage = new ChatMessage(ChatRole.User, x.Message.Content);
    userMessage.SetSenderId(x.Message.Sender);
    userMessage.SetTimestamp(DateTimeOffset.UtcNow);
    if (memoryRecallHook is not null)
    {
        await memoryRecallHook.EnrichAsync(userMessage, x.Message.Sender, x.Message.ConversationId, x.Message.AgentId, thread, linkedCt);
    }

    await warmup;
    var targets = await ResolveDeliveryTargetsAsync(x.Message, x.Channel, channels, linkedCt);
    // ReSharper disable once AccessToDisposedClosure
    return agent
        .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
        .WithErrorHandling(linkedCt)
        .ToUpdateAiResponsePairs()
        .Append((new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null))
        .Select(pair => (pair.Item1, pair.Item2, targets));
```

And replace the delivery loop:

```csharp
await foreach (var (update, _, targets) in aiResponses.WithCancellation(ct))
{
    foreach (var mapped in MapResponseUpdate(update))
    {
        foreach (var target in targets)
        {
            await target.Channel.SendReplyAsync(
                target.ConversationId, mapped.Content, mapped.ContentType, mapped.IsComplete, update.MessageId, ct);
        }
    }

    foreach (var error in update.Contents.OfType<ErrorContent>())
    {
        await metricsPublisher.PublishAsync(new ErrorEvent
        {
            Service = "agent",
            ErrorType = error.ErrorCode ?? "Unknown",
            Message = error.Message
        }, ct);
    }

    yield return true;
}
```

> `channels` is the primary-constructor parameter already on `ChatMonitor`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorDeliveryTests"`
Expected: PASS (3 tests). Also run any existing `ChatMonitor` tests to confirm no regression: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitor"`.

- [ ] **Step 5: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs
git commit -m "feat(chatmonitor): multi-target reply fan-out with conversation minting"
```

## Task 4: ChatMonitor schedule-origin metric emission

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs`
- Test: `Tests/Unit/Domain/Monitor/ChatMonitorScheduleMetricsTests.cs`

When a run's message has `Origin.Kind == "schedule"`, publish a `ScheduleExecutionEvent` on completion. Extract the decision into a pure helper so it's unit-testable without running the agent.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Monitor;
using Xunit;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorScheduleMetricsTests
{
    [Fact]
    public void Builds_event_for_schedule_origin()
    {
        var msg = new ChannelMessage
        {
            ConversationId = "c", Content = "do the thing", Sender = "scheduler", ChannelId = "scheduling",
            AgentId = "jonas", Origin = new MessageOrigin("schedule", "morning-news")
        };

        var evt = ChatMonitor.BuildScheduleEvent(msg, durationMs: 1234, success: true, error: null);

        Assert.NotNull(evt);
        Assert.Equal("morning-news", evt!.ScheduleId);
        Assert.Equal("jonas", evt.AgentId);
        Assert.Equal("do the thing", evt.Prompt);
        Assert.Equal(1234, evt.DurationMs);
        Assert.True(evt.Success);
    }

    [Fact]
    public void Returns_null_for_non_schedule_messages()
    {
        var msg = new ChannelMessage { ConversationId = "c", Content = "hi", Sender = "u", ChannelId = "signalr" };
        Assert.Null(ChatMonitor.BuildScheduleEvent(msg, 1, true, null));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorScheduleMetricsTests"`
Expected: FAIL — `ChatMonitor.BuildScheduleEvent` not defined.

- [ ] **Step 3: Add the helper and emit on completion**

Add to `ChatMonitor` (note `AgentId` is inherited from `MetricEvent`; set it here):

```csharp
public static ScheduleExecutionEvent? BuildScheduleEvent(
    ChannelMessage message, long durationMs, bool success, string? error)
{
    if (message.Origin is not { Kind: "schedule", ScheduleId: { } scheduleId })
    {
        return null;
    }

    return new ScheduleExecutionEvent
    {
        ScheduleId = scheduleId,
        AgentId = message.AgentId ?? "default",
        Prompt = message.Content,
        DurationMs = durationMs,
        Success = success,
        Error = error
    };
}
```

In the `default:` branch of the `aiResponses` projection (from Task 3), wrap the agent stream with timing+error capture when the message is schedule-origin, publishing the event after `StreamCompleteContent`. Insert before the `return`:

```csharp
    var scheduleOrigin = x.Message.Origin is { Kind: "schedule" } ? x.Message : null;
    var stopwatch = scheduleOrigin is not null ? System.Diagnostics.Stopwatch.StartNew() : null;
    var sawError = false;
```

and replace the returned stream's `.Select(...)` tail with a wrapper that records errors and emits on completion:

```csharp
    return agent
        .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
        .WithErrorHandling(linkedCt)
        .ToUpdateAiResponsePairs()
        .Append((new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null))
        .Select(async (pair, _, _) =>
        {
            if (scheduleOrigin is not null)
            {
                if (pair.Item1.Contents.OfType<ErrorContent>().Any())
                {
                    sawError = true;
                }

                if (pair.Item1.Contents.OfType<StreamCompleteContent>().Any())
                {
                    var evt = BuildScheduleEvent(
                        scheduleOrigin, stopwatch!.ElapsedMilliseconds, !sawError,
                        sawError ? "Agent run reported an error" : null);
                    if (evt is not null)
                    {
                        await metricsPublisher.PublishAsync(evt, linkedCt);
                    }
                }
            }

            return (pair.Item1, pair.Item2, targets);
        })
        .Merge(linkedCt);
```

> If the existing `.Select` overload with an async selector + `.Merge` isn't available in this position, instead emit the event in the delivery loop by tracking a `Dictionary<string, (Stopwatch, bool)>` keyed by `scheduleId`; either is acceptable. Keep `BuildScheduleEvent` as the single source of the event shape.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorScheduleMetricsTests"`
Expected: PASS (2 tests). Re-run `~ChatMonitor` to confirm no regressions.

- [ ] **Step 5: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/Monitor/ChatMonitorScheduleMetricsTests.cs
git commit -m "feat(chatmonitor): emit ScheduleExecutionEvent for schedule-origin runs"
```

---

# Phase 2 — McpServerScheduling: channel role + migration

## Task 5: Remove old in-process scheduling + switch Schedule to AgentId

**Files:**
- Delete: `Domain/Monitor/ScheduleExecutor.cs`, `Domain/Monitor/ScheduleDispatcher.cs`, `Agent/App/ScheduleMonitoring.cs`, `Domain/Contracts/IScheduleAgentFactory.cs`, `Domain/Tools/Scheduling/{ScheduleCreateTool,ScheduleListTool,ScheduleDeleteTool,SchedulingToolFeature}.cs`, `Agent/Modules/SchedulingModule.cs`, and the obsolete tests `Tests/Unit/Domain/Scheduling/{ScheduleCreateToolTests,ScheduleListToolTests,SchedulingToolFeatureTests,ScheduleExecutorTests}.cs`
- Modify: `Domain/DTOs/Schedule.cs`, `Agent/Modules/InjectorModule.cs`, the composition root that calls `AddScheduling()`
- Test: `Tests/Unit/Domain/DTOs/ScheduleDtoTests.cs`

This is one atomic refactor so the build stays green: every file that references `schedule.Agent` is being deleted, so removing them together with the DTO change avoids a broken-build window. `AgentId` replaces the embedded `AgentDefinition` (the server only knows agent ids). Add `DeliverTo`. Remove `ScheduleSummary` (only the deleted `ScheduleListTool` used it).

- [ ] **Step 0: Delete the in-process scheduling path and wiring**

```bash
git rm Domain/Monitor/ScheduleExecutor.cs Domain/Monitor/ScheduleDispatcher.cs Agent/App/ScheduleMonitoring.cs Domain/Contracts/IScheduleAgentFactory.cs \
  Domain/Tools/Scheduling/ScheduleCreateTool.cs Domain/Tools/Scheduling/ScheduleListTool.cs Domain/Tools/Scheduling/ScheduleDeleteTool.cs Domain/Tools/Scheduling/SchedulingToolFeature.cs \
  Agent/Modules/SchedulingModule.cs \
  Tests/Unit/Domain/Scheduling/ScheduleCreateToolTests.cs Tests/Unit/Domain/Scheduling/ScheduleListToolTests.cs Tests/Unit/Domain/Scheduling/SchedulingToolFeatureTests.cs Tests/Unit/Domain/Scheduling/ScheduleExecutorTests.cs
```

Then remove the wiring:
- In `Agent/Modules/InjectorModule.cs`, delete the `.AddSingleton<IScheduleAgentFactory>(...)` line (last line of `AddAgent`).
- Remove the `AddScheduling()` call: `grep -rn "AddScheduling" Agent` → delete the call site.
- If `MultiAgentFactory` declares `: IScheduleAgentFactory` or has a `CreateFromDefinition` member, remove them: `grep -rn "IScheduleAgentFactory\|CreateFromDefinition" Infrastructure Agent`.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs;
using Xunit;

namespace Tests.Unit.Domain.DTOs;

public class ScheduleDtoTests
{
    [Fact]
    public void Schedule_uses_agentId_and_optional_deliverTo()
    {
        var s = new Schedule
        {
            Id = "morning-news",
            AgentId = "jonas",
            Prompt = "summarize news",
            CronExpression = "0 8 * * *",
            DeliverTo = ["signalr", "telegram"],
            CreatedAt = DateTime.UtcNow
        };

        Assert.Equal("jonas", s.AgentId);
        Assert.Equal(2, s.DeliverTo!.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleDtoTests"`
Expected: FAIL — `AgentId`/`DeliverTo` not defined (currently `Agent` of type `AgentDefinition`).

- [ ] **Step 3: Rewrite the DTO**

`Domain/DTOs/Schedule.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record Schedule
{
    public required string Id { get; init; }
    public required string AgentId { get; init; }
    public required string Prompt { get; init; }
    public string? CronExpression { get; init; }
    public DateTime? RunAt { get; init; }
    public string? UserId { get; init; }
    public IReadOnlyList<string>? DeliverTo { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextRunAt { get; init; }
}
```

- [ ] **Step 4: Build the whole solution green**

Run: `dotnet build agent.sln`
Expected: succeeds — the only references to `schedule.Agent` were in the files deleted in Step 0. Then:

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleDtoTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(scheduling): remove in-process scheduling path; Schedule uses AgentId + DeliverTo"
```

## Task 6: Scaffold McpServerScheduling project

**Files:**
- Create: `McpServerScheduling/McpServerScheduling.csproj`
- Create: `McpServerScheduling/Program.cs`
- Create: `McpServerScheduling/appsettings.json`
- Create: `McpServerScheduling/Dockerfile`
- Modify: `agent.sln`

No test (scaffolding); verified by build.

- [ ] **Step 1: Create the csproj** (`McpServerScheduling/McpServerScheduling.csproj`) — references Infrastructure (for store/cron) and inherits Domain transitively:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <LangVersion>14</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="..\.dockerignore">
      <Link>.dockerignore</Link>
    </Content>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.7" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.2.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Infrastructure\Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Tests" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create `Program.cs`** (depends on `ConfigModule` from Task 7 — stub the call now, fill in Task 7):

```csharp
using McpServerScheduling.Modules;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureScheduling(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();
```

- [ ] **Step 3: Create `appsettings.json`**:

```json
{
  "RedisConnectionString": "redis:6379",
  "DispatchIntervalSeconds": 30,
  "DefaultDeliverTo": [ "signalr" ],
  "Agents": []
}
```

- [ ] **Step 4: Create `Dockerfile`** (copy the standard MCP template, project name substituted):

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerScheduling/McpServerScheduling.csproj", "McpServerScheduling/"]
RUN dotnet restore "McpServerScheduling/McpServerScheduling.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerScheduling/", "McpServerScheduling/"]
WORKDIR "/src/McpServerScheduling"
RUN dotnet publish "./McpServerScheduling.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerScheduling.dll"]
```

- [ ] **Step 5: Add the project to `agent.sln`** and build.

Add a `Project(...)`/`EndProject` block with a fresh GUID and the matching 12-line config block under `GlobalSection(ProjectConfigurationPlatforms)`. Easiest reliable way:

Run: `dotnet sln agent.sln add McpServerScheduling/McpServerScheduling.csproj`
Then: `dotnet build McpServerScheduling/McpServerScheduling.csproj`
Expected: FAIL — `ConfigModule`/`GetSettings`/`ConfigureScheduling` not found (filled in Task 7). That's expected; commit the scaffold.

```bash
git add McpServerScheduling/ agent.sln
git commit -m "chore(scheduling): scaffold McpServerScheduling project"
```

## Task 7: Settings + ConfigModule (Redis, store, cron, MCP host)

**Files:**
- Create: `McpServerScheduling/Settings/SchedulingSettings.cs`
- Create: `McpServerScheduling/Modules/ConfigModule.cs`
- Test: `Tests/Unit/McpServerScheduling/SchedulingSettingsTests.cs`

- [ ] **Step 1: Write the failing test** (settings binding shape):

```csharp
using McpServerScheduling.Settings;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class SchedulingSettingsTests
{
    [Fact]
    public void Binds_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RedisConnectionString"] = "redis:6379",
            ["DispatchIntervalSeconds"] = "15",
            ["DefaultDeliverTo:0"] = "signalr",
            ["Agents:0:Id"] = "jonas",
            ["Agents:0:Name"] = "Jonas",
            ["Agents:0:Description"] = "General assistant"
        }).Build();

        var settings = config.Get<SchedulingSettings>()!;

        Assert.Equal("redis:6379", settings.RedisConnectionString);
        Assert.Equal(15, settings.DispatchIntervalSeconds);
        Assert.Equal("signalr", settings.DefaultDeliverTo[0]);
        Assert.Equal("jonas", settings.Agents[0].Id);
        Assert.Equal("General assistant", settings.Agents[0].Description);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SchedulingSettingsTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Create settings + config module**

`McpServerScheduling/Settings/SchedulingSettings.cs`:

```csharp
namespace McpServerScheduling.Settings;

public record SchedulingSettings
{
    public required string RedisConnectionString { get; init; }
    public int DispatchIntervalSeconds { get; init; } = 30;
    public IReadOnlyList<string> DefaultDeliverTo { get; init; } = [];
    public IReadOnlyList<SchedulingAgentConfig> Agents { get; init; } = [];
}

public record SchedulingAgentConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
```

`McpServerScheduling/Modules/ConfigModule.cs` (mirrors `McpChannelServiceBus/Modules/ConfigModule.cs`; registers Redis, the store, cron, emitter, dispatcher, the agent catalog, the VFS engine, the MCP tools + resource, and the error filter). References to `ScheduleNotificationEmitter`, `ScheduleDispatcherService`, the `Fs*Tool`s, `SendReplyTool`, `RequestApprovalTool`, `FileSystemResource`, `ScheduleAgentCatalog`, and `ScheduleFileSystem` are added in later tasks — register them here and they compile once those tasks land. For this task, register only what exists (Redis + store + cron + settings + bare MCP host) and grow it:

```csharp
using Domain.Contracts;
using Infrastructure.StateManagers;
using Infrastructure.Validation;
using McpServerScheduling.Settings;
using ModelContextProtocol.Protocol;
using StackExchange.Redis;

namespace McpServerScheduling.Modules;

public static class ConfigModule
{
    public static SchedulingSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .Build();

        return config.Get<SchedulingSettings>()
               ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureScheduling(this IServiceCollection services, SchedulingSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
            .AddSingleton<IScheduleStore, RedisScheduleStore>()
            .AddSingleton<ICronValidator, CronValidator>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<SchedulingSettings>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = ex.Message }]
                    };
                }
            }));

        return services;
    }
}
```

- [ ] **Step 4: Run test + build**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SchedulingSettingsTests"`
Expected: PASS. `dotnet build McpServerScheduling/McpServerScheduling.csproj` → succeeds.

- [ ] **Step 5: Commit**

```bash
git add McpServerScheduling/Settings/SchedulingSettings.cs McpServerScheduling/Modules/ConfigModule.cs Tests/Unit/McpServerScheduling/SchedulingSettingsTests.cs
git commit -m "feat(scheduling): settings + base ConfigModule with Redis/store/cron/MCP host"
```

## Task 8: ScheduleNotificationEmitter

**Files:**
- Create: `McpServerScheduling/Services/ScheduleNotificationEmitter.cs`
- Modify: `McpServerScheduling/Modules/ConfigModule.cs` (register emitter + `RunSessionHandler`)
- Test: `Tests/Unit/McpServerScheduling/ScheduleNotificationPayloadTests.cs`

Mirror `ChannelNotificationEmitter` but build a payload carrying `replyTo` (list) and `origin`. Extract payload construction into a static `BuildPayload` so it's testable without live MCP sessions.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Domain.DTOs.Channel;
using McpServerScheduling.Services;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleNotificationPayloadTests
{
    [Fact]
    public void Payload_includes_replyto_and_origin()
    {
        var node = ScheduleNotificationEmitter.BuildPayload(
            conversationId: "fire-1",
            sender: "scheduler",
            content: "do it",
            agentId: "jonas",
            replyTo: [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-1")],
            origin: new MessageOrigin("schedule", "morning-news"));

        var json = JsonSerializer.Serialize(node,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("jonas", root.GetProperty("agentId").GetString());
        Assert.Equal(2, root.GetProperty("replyTo").GetArrayLength());
        Assert.Equal("schedule", root.GetProperty("origin").GetProperty("kind").GetString());
    }
}
```

> The MCP SDK serializes notification payloads with camelCase. `BuildPayload` returns a strongly-typed record; the test serializes it the same way to assert the wire shape.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleNotificationPayloadTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Implement the emitter**

`McpServerScheduling/Services/ScheduleNotificationEmitter.cs`:

```csharp
using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerScheduling.Services;

public sealed record SchedulePayload(
    string ConversationId,
    string Sender,
    string Content,
    string AgentId,
    IReadOnlyList<ReplyTarget> ReplyTo,
    MessageOrigin Origin,
    DateTimeOffset Timestamp);

public sealed class ScheduleNotificationEmitter(ILogger<ScheduleNotificationEmitter> logger)
{
    private readonly ConcurrentDictionary<string, McpServer> _activeSessions = new();

    public void RegisterSession(string sessionId, McpServer server) => _activeSessions[sessionId] = server;

    public void UnregisterSession(string sessionId) => _activeSessions.TryRemove(sessionId, out _);

    public bool HasActiveSessions => !_activeSessions.IsEmpty;

    public static SchedulePayload BuildPayload(
        string conversationId, string sender, string content, string agentId,
        IReadOnlyList<ReplyTarget> replyTo, MessageOrigin origin) =>
        new(conversationId, sender, content, agentId, replyTo, origin, DateTimeOffset.UtcNow);

    public async Task EmitAsync(SchedulePayload payload, CancellationToken ct = default)
    {
        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync("notifications/channel/message", payload, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to emit channel/message notification");
            }
        });

        await Task.WhenAll(tasks);
    }
}
```

In `ConfigModule.ConfigureScheduling`, construct the emitter and register the session handler (mirror ServiceBus). Add before `.AddMcpServer()`:

```csharp
var emitter = new ScheduleNotificationEmitter(
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ScheduleNotificationEmitter>());
services.AddSingleton(emitter);
```

and change `.WithHttpTransport()` to register sessions:

```csharp
.WithHttpTransport(options =>
{
#pragma warning disable MCPEXP002
    options.RunSessionHandler = async (_, server, ct) =>
    {
        var sessionId = server.SessionId ?? Guid.NewGuid().ToString();
        emitter.RegisterSession(sessionId, server);
        try { await server.RunAsync(ct); }
        finally { emitter.UnregisterSession(sessionId); }
    };
#pragma warning restore MCPEXP002
})
```

Add `using McpServerScheduling.Services;` to `ConfigModule.cs`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleNotificationPayloadTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpServerScheduling/Services/ScheduleNotificationEmitter.cs McpServerScheduling/Modules/ConfigModule.cs Tests/Unit/McpServerScheduling/ScheduleNotificationPayloadTests.cs
git commit -m "feat(scheduling): channel/message emitter with replyTo/origin"
```

## Task 9: ScheduleDispatcherService (cron loop)

**Files:**
- Create: `McpServerScheduling/Services/ScheduleDispatcherService.cs`
- Create: `McpServerScheduling/Services/ScheduleFirePlanner.cs` (pure logic, testable)
- Modify: `McpServerScheduling/Modules/ConfigModule.cs` (register hosted service)
- Test: `Tests/Unit/McpServerScheduling/ScheduleFirePlannerTests.cs`

The pure planner decides, for a due schedule, the next-run advance, whether to delete (one-shot), and the payload (agentId, replyTo from `DeliverTo` or default, origin). The `BackgroundService` polls and drives it.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs;
using McpServerScheduling.Services;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleFirePlannerTests
{
    [Fact]
    public void Recurring_advances_next_run_and_does_not_delete()
    {
        var s = new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc));

        Assert.False(plan.DeleteAfterFire);
        Assert.Equal(new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc), plan.NextRunAt);
        Assert.Equal("jonas", plan.Payload.AgentId);
        Assert.Equal("signalr", plan.Payload.ReplyTo[0].ChannelId);
        Assert.Equal("schedule", plan.Payload.Origin.Kind);
        Assert.Equal("n", plan.Payload.Origin.ScheduleId);
    }

    [Fact]
    public void OneShot_deletes_and_uses_schedule_deliverTo()
    {
        var s = new Schedule { Id = "once", AgentId = "jack", Prompt = "p", RunAt = DateTime.UtcNow, DeliverTo = ["telegram"], CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null);

        Assert.True(plan.DeleteAfterFire);
        Assert.Null(plan.NextRunAt);
        Assert.Equal("telegram", plan.Payload.ReplyTo[0].ChannelId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleFirePlannerTests"`
Expected: FAIL — types not found.

- [ ] **Step 3: Implement planner + hosted service**

`McpServerScheduling/Services/ScheduleFirePlanner.cs`:

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpServerScheduling.Services;

public sealed record FirePlan(SchedulePayload Payload, DateTime? NextRunAt, bool DeleteAfterFire);

public static class ScheduleFirePlanner
{
    public static FirePlan Plan(Schedule schedule, IReadOnlyList<string> defaultDeliverTo, DateTime? nextRun)
    {
        var channels = schedule.DeliverTo is { Count: > 0 } ? schedule.DeliverTo : defaultDeliverTo;
        var replyTo = channels.Select(c => new ReplyTarget(c, null)).ToList();
        var origin = new MessageOrigin("schedule", schedule.Id);

        var payload = ScheduleNotificationEmitter.BuildPayload(
            conversationId: $"sched-{schedule.Id}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            sender: "scheduler",
            content: schedule.Prompt,
            agentId: schedule.AgentId,
            replyTo: replyTo,
            origin: origin);

        var deleteAfterFire = schedule.CronExpression is null;
        return new FirePlan(payload, nextRun, deleteAfterFire);
    }
}
```

`McpServerScheduling/Services/ScheduleDispatcherService.cs`:

```csharp
using Domain.Contracts;
using McpServerScheduling.Settings;

namespace McpServerScheduling.Services;

public sealed class ScheduleDispatcherService(
    IScheduleStore store,
    ICronValidator cronValidator,
    ScheduleNotificationEmitter emitter,
    SchedulingSettings settings,
    ILogger<ScheduleDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(settings.DispatchIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DispatchDueAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error dispatching due schedules");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DispatchDueAsync(CancellationToken ct)
    {
        if (!emitter.HasActiveSessions)
        {
            return;
        }

        var due = await store.GetDueSchedulesAsync(DateTime.UtcNow, ct);
        foreach (var schedule in due)
        {
            var nextRun = schedule.CronExpression is null
                ? null
                : cronValidator.GetNextOccurrence(schedule.CronExpression, DateTime.UtcNow);

            var plan = ScheduleFirePlanner.Plan(schedule, settings.DefaultDeliverTo, nextRun);

            if (plan.DeleteAfterFire)
            {
                await store.DeleteAsync(schedule.Id, ct);
            }
            else
            {
                await store.UpdateLastRunAsync(schedule.Id, DateTime.UtcNow, plan.NextRunAt, ct);
            }

            await emitter.EmitAsync(plan.Payload, ct);
            logger.LogInformation("Fired schedule {ScheduleId} for agent {AgentId}", schedule.Id, schedule.AgentId);
        }
    }
}
```

Register in `ConfigModule.ConfigureScheduling`: `.AddHostedService<ScheduleDispatcherService>()` (add to the first `services` chain).

- [ ] **Step 4: Run test + build**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleFirePlannerTests"` → PASS.
`dotnet build McpServerScheduling/McpServerScheduling.csproj` → succeeds.

- [ ] **Step 5: Commit**

```bash
git add McpServerScheduling/Services/ScheduleFirePlanner.cs McpServerScheduling/Services/ScheduleDispatcherService.cs McpServerScheduling/Modules/ConfigModule.cs Tests/Unit/McpServerScheduling/ScheduleFirePlannerTests.cs
git commit -m "feat(scheduling): cron dispatcher BackgroundService + fire planner"
```

## Task 10: send_reply + request_approval tools (auto-approve)

**Files:**
- Create: `McpServerScheduling/McpTools/SendReplyTool.cs`
- Create: `McpServerScheduling/McpTools/RequestApprovalTool.cs`
- Modify: `McpServerScheduling/Modules/ConfigModule.cs` (`.WithTools<...>()`)
- Test: `Tests/Unit/McpServerScheduling/RequestApprovalToolTests.cs`

`send_reply` is reached only when a fired message has empty `ReplyTo` (no delivery target). For v1 it logs and drops (per spec out-of-scope). `request_approval` auto-approves like ServiceBus.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs;
using McpServerScheduling.McpTools;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class RequestApprovalToolTests
{
    [Fact]
    public void Request_mode_auto_approves()
        => Assert.Equal("approved", RequestApprovalTool.McpRun("c1", ApprovalMode.Request, "[]"));

    [Fact]
    public void Notify_mode_returns_notified()
        => Assert.Equal("notified", RequestApprovalTool.McpRun("c1", ApprovalMode.Notify, "[]"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Implement the tools**

`McpServerScheduling/McpTools/RequestApprovalTool.cs`:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = "request_approval")]
    [Description("Request tool approval — scheduling auto-approves all tools")]
    public static string McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("JSON array of tool requests")] string requests)
        => mode == ApprovalMode.Notify ? "notified" : "approved";
}
```

`McpServerScheduling/McpTools/SendReplyTool.cs`:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = "send_reply")]
    [Description("Receive a reply chunk — scheduling has no inbound surface; chunks are dropped")]
    public static string McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID")] string? messageId)
        => "ok";
}
```

Register in `ConfigModule`: `.WithTools<SendReplyTool>().WithTools<RequestApprovalTool>()` on the `AddMcpServer()` chain.

- [ ] **Step 4: Run test + build** → PASS / succeeds.

- [ ] **Step 5: Commit**

```bash
git add McpServerScheduling/McpTools/SendReplyTool.cs McpServerScheduling/McpTools/RequestApprovalTool.cs McpServerScheduling/Modules/ConfigModule.cs Tests/Unit/McpServerScheduling/RequestApprovalToolTests.cs
git commit -m "feat(scheduling): send_reply + auto-approve request_approval tools"
```

## Task 11: Checkpoint — channel migration builds and old path is gone

The in-process firing path and tools were deleted atomically in Task 5. This is a verification checkpoint after the new server's channel role exists (Tasks 6–10): confirm the whole system builds and nothing references the removed types.

- [ ] **Step 1: Build everything**

Run: `dotnet build agent.sln`
Expected: succeeds (Agent + `McpServerScheduling` + Tests).

- [ ] **Step 2: Confirm no stragglers**

Run: `grep -rn "ScheduleExecutor\|ScheduleDispatcher\|ScheduleMonitoring\|IScheduleAgentFactory" Domain Agent Infrastructure`
Expected: no results. If `MultiAgentFactory` still declares `: IScheduleAgentFactory` or a `CreateFromDefinition` member, remove them and rebuild.

- [ ] **Step 3: Run the channel-side unit tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitor|FullyQualifiedName~ScheduleFirePlanner|FullyQualifiedName~ScheduleNotificationPayload"`
Expected: PASS.

- [ ] **Step 4: Commit** (only if Step 2 required a fix)

```bash
git add -A
git commit -m "refactor(scheduling): drop residual IScheduleAgentFactory usage"
```

---

# Phase 3 — Filesystem read surface

## Task 12: Agent catalog contract + DTO

**Files:**
- Create: `Domain/Contracts/IScheduleAgentCatalog.cs`
- Create: `Domain/DTOs/ScheduleAgentInfo.cs`
- Create: `McpServerScheduling/Services/ScheduleAgentCatalog.cs` (config-backed impl)
- Modify: `McpServerScheduling/Modules/ConfigModule.cs` (register catalog)
- Test: `Tests/Unit/McpServerScheduling/ScheduleAgentCatalogTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using McpServerScheduling.Services;
using McpServerScheduling.Settings;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleAgentCatalogTests
{
    [Fact]
    public void Lists_agents_and_resolves_known()
    {
        var catalog = new ScheduleAgentCatalog(new SchedulingSettings
        {
            RedisConnectionString = "x",
            Agents = [ new SchedulingAgentConfig { Id = "jonas", Name = "Jonas", Description = "general" } ]
        });

        Assert.Single(catalog.GetAll());
        Assert.True(catalog.Exists("jonas"));
        Assert.False(catalog.Exists("ghost"));
        Assert.Equal("Jonas", catalog.Get("jonas")!.Name);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleAgentCatalogTests"`
Expected: FAIL — types not found.

- [ ] **Step 3: Implement contract, DTO, and impl**

`Domain/DTOs/ScheduleAgentInfo.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ScheduleAgentInfo(string Id, string Name, string? Description);
```

`Domain/Contracts/IScheduleAgentCatalog.cs`:

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface IScheduleAgentCatalog
{
    IReadOnlyList<ScheduleAgentInfo> GetAll();
    ScheduleAgentInfo? Get(string agentId);
    bool Exists(string agentId);
}
```

`McpServerScheduling/Services/ScheduleAgentCatalog.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using McpServerScheduling.Settings;

namespace McpServerScheduling.Services;

public sealed class ScheduleAgentCatalog(SchedulingSettings settings) : IScheduleAgentCatalog
{
    private readonly IReadOnlyList<ScheduleAgentInfo> _agents = settings.Agents
        .Select(a => new ScheduleAgentInfo(a.Id, a.Name, a.Description))
        .ToList();

    public IReadOnlyList<ScheduleAgentInfo> GetAll() => _agents;
    public ScheduleAgentInfo? Get(string agentId) => _agents.FirstOrDefault(a => a.Id == agentId);
    public bool Exists(string agentId) => _agents.Any(a => a.Id == agentId);
}
```

Register in `ConfigModule`: `.AddSingleton<IScheduleAgentCatalog, ScheduleAgentCatalog>()`.

- [ ] **Step 4: Run test + build** → PASS / succeeds.

- [ ] **Step 5: Commit**

```bash
git add Domain/Contracts/IScheduleAgentCatalog.cs Domain/DTOs/ScheduleAgentInfo.cs McpServerScheduling/Services/ScheduleAgentCatalog.cs McpServerScheduling/Modules/ConfigModule.cs Tests/Unit/McpServerScheduling/ScheduleAgentCatalogTests.cs
git commit -m "feat(scheduling): config-backed agent catalog"
```

## Task 13: SchedulePath resolver (pure)

**Files:**
- Create: `Domain/Tools/Scheduling/Vfs/SchedulePath.cs`
- Test: `Tests/Unit/Domain/Scheduling/Vfs/SchedulePathTests.cs`

Resolves a virtual path (already mount-relative, e.g. `/jonas/morning-news/schedule.json`) to a typed node.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Tools.Scheduling.Vfs;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class SchedulePathTests
{
    [Theory]
    [InlineData("/", ScheduleNodeKind.Root)]
    [InlineData("/jonas", ScheduleNodeKind.AgentDir)]
    [InlineData("/jonas/agent_info.json", ScheduleNodeKind.AgentInfoFile)]
    [InlineData("/jonas/morning-news", ScheduleNodeKind.ScheduleDir)]
    [InlineData("/jonas/morning-news/schedule.json", ScheduleNodeKind.ScheduleFile)]
    [InlineData("/jonas/morning-news/status.json", ScheduleNodeKind.StatusFile)]
    [InlineData("/jonas/morning-news/run_now.sh", ScheduleNodeKind.RunNowFile)]
    [InlineData("/jonas/morning-news/bogus", ScheduleNodeKind.Unknown)]
    public void Resolves_node_kinds(string path, ScheduleNodeKind expected)
    {
        var node = SchedulePath.Parse(path);
        Assert.Equal(expected, node.Kind);
    }

    [Fact]
    public void Captures_segments()
    {
        var node = SchedulePath.Parse("/jonas/morning-news/schedule.json");
        Assert.Equal("jonas", node.AgentId);
        Assert.Equal("morning-news", node.ScheduleId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SchedulePathTests"`
Expected: FAIL — types not found.

- [ ] **Step 3: Implement the resolver**

`Domain/Tools/Scheduling/Vfs/SchedulePath.cs`:

```csharp
namespace Domain.Tools.Scheduling.Vfs;

public enum ScheduleNodeKind
{
    Root, AgentDir, AgentInfoFile, ScheduleDir, ScheduleFile, StatusFile, RunNowFile, Unknown
}

public sealed record ScheduleNode(ScheduleNodeKind Kind, string? AgentId, string? ScheduleId);

public static class SchedulePath
{
    public const string ScheduleFileName = "schedule.json";
    public const string StatusFileName = "status.json";
    public const string AgentInfoFileName = "agent_info.json";
    public const string RunNowFileName = "run_now.sh";

    public static ScheduleNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments switch
        {
            [] => new ScheduleNode(ScheduleNodeKind.Root, null, null),
            [var agent] => new ScheduleNode(ScheduleNodeKind.AgentDir, agent, null),
            [var agent, AgentInfoFileName] => new ScheduleNode(ScheduleNodeKind.AgentInfoFile, agent, null),
            [var agent, var sched] => new ScheduleNode(ScheduleNodeKind.ScheduleDir, agent, sched),
            [var agent, var sched, ScheduleFileName] => new ScheduleNode(ScheduleNodeKind.ScheduleFile, agent, sched),
            [var agent, var sched, StatusFileName] => new ScheduleNode(ScheduleNodeKind.StatusFile, agent, sched),
            [var agent, var sched, RunNowFileName] => new ScheduleNode(ScheduleNodeKind.RunNowFile, agent, sched),
            _ => new ScheduleNode(ScheduleNodeKind.Unknown, null, null)
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes** → PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Scheduling/Vfs/SchedulePath.cs Tests/Unit/Domain/Scheduling/Vfs/SchedulePathTests.cs
git commit -m "feat(scheduling): VFS path resolver"
```

## Task 14: ScheduleFileSystem engine — glob/info/read

**Files:**
- Create: `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs`
- Test: `Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemReadTests.cs`
- Test helper: `Tests/Unit/Domain/Scheduling/Vfs/FakeScheduleStore.cs`

The engine returns `JsonNode` envelopes: success via `FsResultContract.ToNode(<dto>)`, errors via `ToolError.Create(...)`. This task implements `GlobAsync`, `InfoAsync`, `ReadAsync`.

- [ ] **Step 1: Write the failing test + fake store**

`Tests/Unit/Domain/Scheduling/Vfs/FakeScheduleStore.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public sealed class FakeScheduleStore : IScheduleStore
{
    public readonly Dictionary<string, Schedule> Items = new();

    public Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default)
    {
        Items[schedule.Id] = schedule;
        return Task.FromResult(schedule);
    }

    public Task<Schedule?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.GetValueOrDefault(id));

    public Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Schedule>>(Items.Values.ToList());

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        Items.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Schedule>>(Items.Values.Where(s => s.NextRunAt <= asOf).ToList());

    public Task UpdateLastRunAsync(string id, DateTime lastRunAt, DateTime? nextRunAt, CancellationToken ct = default)
    {
        if (Items.TryGetValue(id, out var s))
        {
            Items[id] = s with { LastRunAt = lastRunAt, NextRunAt = nextRunAt };
        }

        return Task.CompletedTask;
    }
}
```

`Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemReadTests.cs`:

```csharp
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemReadTests
{
    private static ScheduleFileSystem Build(FakeScheduleStore store)
    {
        var catalog = new FakeAgentCatalog([
            new ScheduleAgentInfo("jonas", "Jonas", "general"),
            new ScheduleAgentInfo("jack", "Jack", "library")
        ]);
        return new ScheduleFileSystem(store, catalog, new CronValidator());
    }

    [Fact]
    public async Task Glob_root_lists_all_agents_including_empty()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.GlobAsync("/", "*", CancellationToken.None);
        var entries = node["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains("/jonas", entries);
        Assert.Contains("/jack", entries);
    }

    [Fact]
    public async Task Glob_agent_lists_its_schedules()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.GlobAsync("/jonas", "*", CancellationToken.None);
        var entries = node["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains("/jonas/morning-news", entries);
    }

    [Fact]
    public async Task Read_agent_info_returns_metadata()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.ReadAsync("/jonas/agent_info.json", null, null, CancellationToken.None);
        Assert.Contains("\"id\"", node["content"]!.GetValue<string>());
        Assert.Contains("Jonas", node["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_schedule_file_returns_spec_without_agentId()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "summarize", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.ReadAsync("/jonas/morning-news/schedule.json", null, null, CancellationToken.None);
        var content = node["content"]!.GetValue<string>();
        Assert.Contains("summarize", content);
        Assert.DoesNotContain("agentId", content);
    }

    [Fact]
    public async Task Read_unknown_agent_returns_not_found_envelope()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.ReadAsync("/ghost/x/schedule.json", null, null, CancellationToken.None);
        Assert.True(ToolErrorResult.IsErrorEnvelope(node));
    }
}
```

Add `Tests/Unit/Domain/Scheduling/Vfs/FakeAgentCatalog.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public sealed class FakeAgentCatalog(IReadOnlyList<ScheduleAgentInfo> agents) : IScheduleAgentCatalog
{
    public IReadOnlyList<ScheduleAgentInfo> GetAll() => agents;
    public ScheduleAgentInfo? Get(string agentId) => agents.FirstOrDefault(a => a.Id == agentId);
    public bool Exists(string agentId) => agents.Any(a => a.Id == agentId);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleFileSystemReadTests"`
Expected: FAIL — `ScheduleFileSystem` not found.

- [ ] **Step 3: Implement the engine (read methods)**

`Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs` (Domain-layer; depends only on contracts; emits the strict FS DTOs):

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;

namespace Domain.Tools.Scheduling.Vfs;

public sealed class ScheduleFileSystem(
    IScheduleStore store,
    IScheduleAgentCatalog agents,
    ICronValidator cronValidator)
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public async Task<JsonNode> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var node = SchedulePath.Parse(basePath);
        switch (node.Kind)
        {
            case ScheduleNodeKind.Root:
            {
                var entries = agents.GetAll().Select(a => $"/{a.Id}").ToList();
                return Glob(entries);
            }
            case ScheduleNodeKind.AgentDir when agents.Exists(node.AgentId!):
            {
                var all = await store.ListAsync(ct);
                var entries = all.Where(s => s.AgentId == node.AgentId)
                    .Select(s => $"/{node.AgentId}/{s.Id}").ToList();
                return Glob(entries);
            }
            default:
                return NotFound(basePath);
        }
    }

    public async Task<JsonNode> InfoAsync(string path, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        var exists = await NodeExistsAsync(node, ct);
        var isDir = node.Kind is ScheduleNodeKind.Root or ScheduleNodeKind.AgentDir or ScheduleNodeKind.ScheduleDir;
        return FsResultContract.ToNode(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? isDir : null
        });
    }

    public async Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        string content;
        switch (node.Kind)
        {
            case ScheduleNodeKind.AgentInfoFile when agents.Get(node.AgentId!) is { } info:
                content = JsonSerializer.Serialize(info, _json);
                break;
            case ScheduleNodeKind.ScheduleFile when await GetScheduleAsync(node, ct) is { } s:
                content = RenderSpec(s);
                break;
            case ScheduleNodeKind.StatusFile when await GetScheduleAsync(node, ct) is { } s:
                content = RenderStatus(s);
                break;
            case ScheduleNodeKind.RunNowFile when await GetScheduleAsync(node, ct) is not null:
                content = "# Run this schedule now:\n#   exec run_now.sh\n";
                break;
            default:
                return NotFound(path);
        }

        return FsResultContract.ToNode(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });
    }

    private static string RenderSpec(Schedule s) => JsonSerializer.Serialize(new
    {
        prompt = s.Prompt,
        cron = s.CronExpression,
        runAt = s.RunAt,
        userId = s.UserId,
        deliverTo = s.DeliverTo
    }, _json);

    private static string RenderStatus(Schedule s) => JsonSerializer.Serialize(new
    {
        createdAt = s.CreatedAt,
        lastRunAt = s.LastRunAt,
        nextRunAt = s.NextRunAt
    }, _json);

    private async Task<Schedule?> GetScheduleAsync(ScheduleNode node, CancellationToken ct)
    {
        if (node.AgentId is null || node.ScheduleId is null || !agents.Exists(node.AgentId))
        {
            return null;
        }

        var s = await store.GetAsync(node.ScheduleId, ct);
        return s is not null && s.AgentId == node.AgentId ? s : null;
    }

    private async Task<bool> NodeExistsAsync(ScheduleNode node, CancellationToken ct) => node.Kind switch
    {
        ScheduleNodeKind.Root => true,
        ScheduleNodeKind.AgentDir or ScheduleNodeKind.AgentInfoFile => agents.Exists(node.AgentId!),
        ScheduleNodeKind.ScheduleDir or ScheduleNodeKind.ScheduleFile
            or ScheduleNodeKind.StatusFile or ScheduleNodeKind.RunNowFile => await GetScheduleAsync(node, ct) is not null,
        _ => false
    };

    private static JsonNode Glob(IReadOnlyList<string> entries) => FsResultContract.ToNode(new FsGlobResult
    {
        Entries = entries,
        Truncated = false,
        Total = entries.Count
    });

    private static JsonNode NotFound(string path) =>
        ToolError.Create(ToolError.Codes.NotFound, $"Path not found: {path}", retryable: false);
}
```

> `ICronValidator` is injected now for use by the write methods (Task 16). It's unused here; that's fine.

- [ ] **Step 4: Run test to verify it passes** → PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs Tests/Unit/Domain/Scheduling/Vfs/
git commit -m "feat(scheduling): VFS engine read surface (glob/info/read)"
```

## Task 15: fs read tools + filesystem resource + DI

**Files:**
- Create: `McpServerScheduling/McpTools/FsGlobTool.cs`, `FsInfoTool.cs`, `FsReadTool.cs`, `FsSearchTool.cs`
- Create: `McpServerScheduling/McpResources/FileSystemResource.cs`
- Modify: `McpServerScheduling/Modules/ConfigModule.cs` (register engine, tools, resource)
- Test: `Tests/Unit/McpServerScheduling/FileSystemResourceTests.cs`

`fs_search` calls a new `SearchAsync` on the engine; add a minimal implementation that greps schedule ids/prompts/agents (returns `FsSearchResult`). Wrappers return `ToolResponse.Create(node)`.

- [ ] **Step 1: Write the failing test (resource metadata shape)**

```csharp
using System.Text.Json;
using McpServerScheduling.McpResources;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class FileSystemResourceTests
{
    [Fact]
    public void Publishes_schedules_mount_metadata()
    {
        var json = new FileSystemResource().GetInfo();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("schedules", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("/schedules", doc.RootElement.GetProperty("mountPoint").GetString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FileSystemResourceTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Implement resource, search, and wrappers**

`McpServerScheduling/McpResources/FileSystemResource.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(UriTemplate = "filesystem://schedules", Name = "Schedules Filesystem", MimeType = "application/json")]
    [Description("Scheduled-task control surface")]
    public string GetInfo() => JsonSerializer.Serialize(new
    {
        name = "schedules",
        mountPoint = "/schedules",
        description = "Scheduled agent tasks grouped by agent: /<agentId>/<scheduleId>/schedule.json (edit), status.json (read-only), run_now.sh (exec). Agent dirs are always listed; read agent_info.json to learn an agent. Create a schedule with fs_create using a descriptive, unique id; reassign with fs_move; supports fs_exec for run_now."
    });
}
```

Add `SearchAsync` to `ScheduleFileSystem`:

```csharp
public async Task<JsonNode> SearchAsync(string query, CancellationToken ct)
{
    var all = await store.ListAsync(ct);
    var hits = all.Where(s =>
        s.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        s.Prompt.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        s.AgentId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

    return FsResultContract.ToNode(new FsSearchResult
    {
        Query = query,
        Regex = false,
        Path = "/",
        FilesSearched = all.Count,
        FilesWithMatches = hits.Count,
        TotalMatches = hits.Count,
        Truncated = false,
        Results = hits.Select(s => new FsSearchFileResult { File = $"/{s.AgentId}/{s.Id}/schedule.json", MatchCount = 1 }).ToList()
    });
}
```

Wrappers (each its own file under `McpServerScheduling/McpTools/`):

```csharp
// FsGlobTool.cs
using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsGlobTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("List schedule filesystem entries matching a glob under basePath")]
    public async Task<CallToolResult> McpRun(string pattern, string basePath = "/", CancellationToken ct = default)
        => ToolResponse.Create(await fs.GlobAsync(basePath, pattern, ct));
}
```

```csharp
// FsInfoTool.cs
using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsInfoTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_info")]
    [Description("Get info about a schedule filesystem path")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.InfoAsync(path, ct));
}
```

```csharp
// FsReadTool.cs
using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsReadTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a schedule filesystem file (schedule.json/status.json/agent_info.json/run_now.sh)")]
    public async Task<CallToolResult> McpRun(string path, int? offset = null, int? limit = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ReadAsync(path, offset, limit, ct));
}
```

```csharp
// FsSearchTool.cs
using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsSearchTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Search schedules by id, prompt, or agent")]
    public async Task<CallToolResult> McpRun(
        string query, bool regex = false, string? path = null, string? directoryPath = null,
        string? filePattern = null, int maxResults = 50, int contextLines = 1, string outputMode = "content",
        CancellationToken ct = default)
        => ToolResponse.Create(await fs.SearchAsync(query, ct));
}
```

Register in `ConfigModule`: `.AddSingleton<ScheduleFileSystem>()` and on the MCP chain `.WithTools<FsGlobTool>().WithTools<FsInfoTool>().WithTools<FsReadTool>().WithTools<FsSearchTool>().WithResources<FileSystemResource>()`. Add the needed `using`s.

- [ ] **Step 4: Run test + build** → PASS / succeeds.

- [ ] **Step 5: Commit**

```bash
git add McpServerScheduling/McpTools/Fs{Glob,Info,Read,Search}Tool.cs McpServerScheduling/McpResources/FileSystemResource.cs McpServerScheduling/Modules/ConfigModule.cs Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs Tests/Unit/McpServerScheduling/FileSystemResourceTests.cs
git commit -m "feat(scheduling): fs read tools + filesystem://schedules resource"
```

---

# Phase 4 — Filesystem write + act surface

## Task 16: Engine write methods — create/edit/delete/move + validation

**Files:**
- Modify: `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs`
- Test: `Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemWriteTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemWriteTests
{
    private static ScheduleFileSystem Build(FakeScheduleStore store) =>
        new(store, new FakeAgentCatalog([new ScheduleAgentInfo("jonas", "Jonas", "general")]), new CronValidator());

    private const string ValidSpec = """{"prompt":"summarize news","cron":"0 8 * * *"}""";

    [Fact]
    public async Task Create_persists_schedule_with_agentId_from_path()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store);

        var node = await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);

        Assert.False(ToolErrorResult.IsErrorEnvelope(node));
        var saved = store.Items["morning-news"];
        Assert.Equal("jonas", saved.AgentId);
        Assert.Equal("0 8 * * *", saved.CronExpression);
        Assert.NotNull(saved.NextRunAt);
    }

    [Fact]
    public async Task Create_unknown_agent_is_rejected()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.CreateAsync("/ghost/x/schedule.json", ValidSpec, false, true, CancellationToken.None);
        Assert.True(ToolErrorResult.IsErrorEnvelope(node));
    }

    [Fact]
    public async Task Create_duplicate_id_is_rejected()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);
        Assert.True(ToolErrorResult.IsErrorEnvelope(node));
    }

    [Fact]
    public async Task Create_with_both_cron_and_runAt_is_rejected()
    {
        var fs = Build(new FakeScheduleStore());
        var spec = """{"prompt":"p","cron":"0 8 * * *","runAt":"2999-01-01T00:00:00Z"}""";
        var node = await fs.CreateAsync("/jonas/x/schedule.json", spec, false, true, CancellationToken.None);
        Assert.True(ToolErrorResult.IsErrorEnvelope(node));
    }

    [Fact]
    public async Task Move_reassigns_agent()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = new ScheduleFileSystem(store,
            new FakeAgentCatalog([new ScheduleAgentInfo("jonas","J",null), new ScheduleAgentInfo("home","Home",null)]),
            new CronValidator());

        var node = await fs.MoveAsync("/jonas/morning-news", "/home/morning-news", CancellationToken.None);

        Assert.False(ToolErrorResult.IsErrorEnvelope(node));
        Assert.Equal("home", store.Items["morning-news"].AgentId);
    }

    [Fact]
    public async Task Delete_removes_schedule()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.DeleteAsync("/jonas/morning-news", CancellationToken.None);

        Assert.False(ToolErrorResult.IsErrorEnvelope(node));
        Assert.False(store.Items.ContainsKey("morning-news"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleFileSystemWriteTests"`
Expected: FAIL — `CreateAsync`/`MoveAsync`/`DeleteAsync`/`EditAsync` not defined.

- [ ] **Step 3: Implement write methods**

Add to `ScheduleFileSystem` (plus `using System.Text.Json.Nodes;` already present):

```csharp
public async Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
{
    var node = SchedulePath.Parse(path);
    if (node.Kind != ScheduleNodeKind.ScheduleFile || node.AgentId is null || node.ScheduleId is null)
    {
        return Invalid($"Create a schedule at /<agentId>/<scheduleId>/schedule.json (got '{path}')");
    }

    if (!agents.Exists(node.AgentId))
    {
        return ToolError.Create(ToolError.Codes.NotFound, $"Unknown agent '{node.AgentId}'", retryable: false);
    }

    if (await store.GetAsync(node.ScheduleId, ct) is not null)
    {
        return ToolError.Create(ToolError.Codes.AlreadyExists, $"Schedule '{node.ScheduleId}' already exists", retryable: false);
    }

    var spec = ParseSpec(content, out var specError);
    if (specError is not null)
    {
        return specError;
    }

    var validation = ValidateSpec(spec!);
    if (validation is not null)
    {
        return validation;
    }

    var schedule = new Schedule
    {
        Id = node.ScheduleId,
        AgentId = node.AgentId,
        Prompt = spec!.Prompt!,
        CronExpression = spec.Cron,
        RunAt = spec.RunAt,
        UserId = spec.UserId,
        DeliverTo = spec.DeliverTo,
        CreatedAt = DateTime.UtcNow,
        NextRunAt = spec.RunAt ?? (spec.Cron is not null ? cronValidator.GetNextOccurrence(spec.Cron, DateTime.UtcNow) : null)
    };

    await store.CreateAsync(schedule, ct);
    return FsResultContract.ToNode(new FsCreateResult
    {
        Status = "created", FilePath = path, Size = content.Length.ToString(), Lines = content.Split('\n').Length
    });
}

public async Task<JsonNode> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
{
    var node = SchedulePath.Parse(path);
    if (node.Kind != ScheduleNodeKind.ScheduleFile || await GetScheduleAsync(node, ct) is not { } existing)
    {
        return NotFound(path);
    }

    var current = RenderSpec(existing);
    var updatedText = edits.Aggregate(current, (acc, e) =>
        e.ReplaceAll ? acc.Replace(e.OldString, e.NewString)
                     : ReplaceFirst(acc, e.OldString, e.NewString));

    var spec = ParseSpec(updatedText, out var specError);
    if (specError is not null)
    {
        return specError;
    }

    var validation = ValidateSpec(spec!);
    if (validation is not null)
    {
        return validation;
    }

    var updated = existing with
    {
        Prompt = spec!.Prompt!,
        CronExpression = spec.Cron,
        RunAt = spec.RunAt,
        UserId = spec.UserId,
        DeliverTo = spec.DeliverTo,
        NextRunAt = spec.RunAt ?? (spec.Cron is not null ? cronValidator.GetNextOccurrence(spec.Cron, DateTime.UtcNow) : null)
    };

    await store.CreateAsync(updated, ct); // CreateAsync is an upsert in the store (StringSet)
    return FsResultContract.ToNode(new FsEditResult
    {
        Status = "edited", FilePath = path, TotalOccurrencesReplaced = edits.Count,
        Edits = edits.Select(_ => new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }).ToList()
    });
}

public async Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
{
    var src = SchedulePath.Parse(sourcePath);
    var dst = SchedulePath.Parse(destinationPath);
    if (src.Kind != ScheduleNodeKind.ScheduleDir || dst.Kind != ScheduleNodeKind.ScheduleDir)
    {
        return Invalid("Move a schedule dir to /<agentId>/<scheduleId>");
    }

    if (await GetScheduleAsync(src, ct) is not { } existing)
    {
        return NotFound(sourcePath);
    }

    if (!agents.Exists(dst.AgentId!))
    {
        return ToolError.Create(ToolError.Codes.NotFound, $"Unknown agent '{dst.AgentId}'", retryable: false);
    }

    if (dst.ScheduleId != src.ScheduleId && await store.GetAsync(dst.ScheduleId!, ct) is not null)
    {
        return ToolError.Create(ToolError.Codes.AlreadyExists, $"Schedule '{dst.ScheduleId}' already exists", retryable: false);
    }

    if (dst.ScheduleId != src.ScheduleId)
    {
        await store.DeleteAsync(src.ScheduleId!, ct);
    }

    await store.CreateAsync(existing with { Id = dst.ScheduleId!, AgentId = dst.AgentId! }, ct);
    return FsResultContract.ToNode(new FsMoveResult
    {
        Status = "moved", Message = "reassigned", Source = sourcePath, Destination = destinationPath
    });
}

public async Task<JsonNode> DeleteAsync(string path, CancellationToken ct)
{
    var node = SchedulePath.Parse(path);
    if (node.Kind != ScheduleNodeKind.ScheduleDir || await GetScheduleAsync(node, ct) is null)
    {
        return NotFound(path);
    }

    await store.DeleteAsync(node.ScheduleId!, ct);
    return FsResultContract.ToNode(new FsRemoveResult
    {
        Status = "deleted", Message = "removed", OriginalPath = path, TrashPath = ""
    });
}

private sealed record SpecDto
{
    public string? Prompt { get; init; }
    public string? Cron { get; init; }
    public DateTime? RunAt { get; init; }
    public string? UserId { get; init; }
    public IReadOnlyList<string>? DeliverTo { get; init; }
}

private static SpecDto? ParseSpec(string content, out JsonNode? error)
{
    error = null;
    try
    {
        var spec = JsonSerializer.Deserialize<SpecDto>(content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (spec is null)
        {
            error = Invalid("schedule.json is empty");
        }

        return spec;
    }
    catch (JsonException ex)
    {
        error = Invalid($"Invalid schedule.json: {ex.Message}");
        return null;
    }
}

private JsonNode? ValidateSpec(SpecDto spec)
{
    if (string.IsNullOrWhiteSpace(spec.Prompt))
    {
        return Invalid("prompt is required");
    }

    if (spec.Cron is null && spec.RunAt is null)
    {
        return Invalid("Provide either cron or runAt");
    }

    if (spec.Cron is not null && spec.RunAt is not null)
    {
        return Invalid("Provide only cron OR runAt, not both");
    }

    if (spec.Cron is not null && !cronValidator.IsValid(spec.Cron))
    {
        return Invalid($"Invalid cron expression: {spec.Cron}");
    }

    if (spec.RunAt is not null && spec.RunAt <= DateTime.UtcNow)
    {
        return Invalid("runAt must be in the future");
    }

    return null;
}

private static string ReplaceFirst(string text, string oldValue, string newValue)
{
    var i = text.IndexOf(oldValue, StringComparison.Ordinal);
    return i < 0 ? text : text[..i] + newValue + text[(i + oldValue.Length)..];
}

private static JsonNode Invalid(string message) =>
    ToolError.Create(ToolError.Codes.InvalidArgument, message, retryable: false);
```

Add `using Domain.DTOs;` is already present; `TextEdit` lives in `Domain.DTOs` (used by `IFileSystemBackend`). Confirm with `grep -rn "record TextEdit" Domain`.

- [ ] **Step 4: Run test to verify it passes** → PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemWriteTests.cs
git commit -m "feat(scheduling): VFS engine write surface (create/edit/delete/move + validation)"
```

## Task 17: Engine exec (run_now) + fs write/exec tool wrappers

**Files:**
- Modify: `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs` (add `ExecAsync`)
- Create: `McpServerScheduling/McpTools/FsCreateTool.cs`, `FsEditTool.cs`, `FsDeleteTool.cs`, `FsMoveTool.cs`, `FsExecTool.cs`
- Modify: `McpServerScheduling/Modules/ConfigModule.cs` (register tools)
- Test: `Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemExecTests.cs`

`ExecAsync` only accepts `run_now.sh`; anything else returns exit 127. Running it advances/deletes via the store and signals the dispatcher to fire on its next tick (set `NextRunAt = now`).

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemExecTests
{
    private static ScheduleFileSystem Build(FakeScheduleStore store) =>
        new(store, new FakeAgentCatalog([new ScheduleAgentInfo("jonas","J",null)]), new CronValidator());

    [Fact]
    public async Task RunNow_marks_schedule_due()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", NextRunAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);

        var result = node.Deserialize<FsExecResult>(FsResultContract.ValidationOptions)!;
        Assert.Equal(0, result.ExitCode);
        Assert.True(store.Items["n"].NextRunAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Unknown_command_returns_127()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.ExecAsync("/jonas/n", "ls -la", null, CancellationToken.None);

        var result = node.Deserialize<FsExecResult>(FsResultContract.ValidationOptions)!;
        Assert.Equal(127, result.ExitCode);
        Assert.Contains("run_now.sh", result.Stderr);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleFileSystemExecTests"`
Expected: FAIL — `ExecAsync` not defined.

- [ ] **Step 3: Implement exec + wrappers**

Add to `ScheduleFileSystem`:

```csharp
public async Task<JsonNode> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
{
    var node = SchedulePath.Parse(path);
    if (node.Kind != ScheduleNodeKind.ScheduleDir || await GetScheduleAsync(node, ct) is not { } schedule)
    {
        return NotFound(path);
    }

    var trimmed = command.Trim();
    if (trimmed != SchedulePath.RunNowFileName)
    {
        return Exec("", $"command not found: {trimmed}\navailable: {SchedulePath.RunNowFileName}", 127, path);
    }

    await store.UpdateLastRunAsync(schedule.Id, schedule.LastRunAt ?? DateTime.UtcNow, DateTime.UtcNow, ct);
    return Exec($"queued '{schedule.Id}' to run now\n", "", 0, path);
}

private static JsonNode Exec(string stdout, string stderr, int exitCode, string cwd) =>
    FsResultContract.ToNode(new FsExecResult
    {
        Stdout = stdout, Stderr = stderr, ExitCode = exitCode,
        Truncated = false, TimedOut = false, DurationMs = 0, Cwd = cwd
    });
```

> Setting `NextRunAt = now` makes the dispatcher pick the schedule up on its next poll and fire it through the normal channel path — no duplicated execution logic.

Create the five wrappers (pattern identical to Task 15; shown for create + exec, the other three mirror delete/move/edit signatures from `McpFileSystemBackend`):

```csharp
// FsCreateTool.cs
using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsCreateTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_create")]
    [Description("Create a schedule: fs_create /<agentId>/<descriptive-id>/schedule.json with JSON {prompt, cron|runAt, userId?, deliverTo?}")]
    public async Task<CallToolResult> McpRun(string path, string content, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
        => ToolResponse.Create(await fs.CreateAsync(path, content, overwrite, createDirectories, ct));
}
```

```csharp
// FsEditTool.cs
using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsEditTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_edit")]
    [Description("Edit a schedule.json (prompt/timing/deliverTo)")]
    public async Task<CallToolResult> McpRun(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default)
        => ToolResponse.Create(await fs.EditAsync(path, edits, ct));
}
```

```csharp
// FsDeleteTool.cs
using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsDeleteTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Delete a schedule directory")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.DeleteAsync(path, ct));
}
```

```csharp
// FsMoveTool.cs
using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsMoveTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_move")]
    [Description("Reassign a schedule to another agent or rename it: move /<agent>/<id> to /<agent2>/<id2>")]
    public async Task<CallToolResult> McpRun(string sourcePath, string destinationPath, CancellationToken ct = default)
        => ToolResponse.Create(await fs.MoveAsync(sourcePath, destinationPath, ct));
}
```

```csharp
// FsExecTool.cs
using System.ComponentModel;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public class FsExecTool(ScheduleFileSystem fs)
{
    [McpServerTool(Name = "fs_exec")]
    [Description("Run a schedule action (run_now.sh fires it immediately)")]
    public async Task<CallToolResult> McpRun(string path, string command, int? timeoutSeconds = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ExecAsync(path, command, timeoutSeconds, ct));
}
```

Register all five in `ConfigModule` on the MCP chain.

- [ ] **Step 4: Run test + build** → PASS / succeeds. Full server build: `dotnet build McpServerScheduling/McpServerScheduling.csproj`.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs McpServerScheduling/McpTools/Fs{Create,Edit,Delete,Move,Exec}Tool.cs McpServerScheduling/Modules/ConfigModule.cs Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemExecTests.cs
git commit -m "feat(scheduling): VFS exec (run_now) + write/exec fs tool wrappers"
```

## Task 18: Checkpoint — domain scheduling tools fully removed

The 3 domain tools, `SchedulingToolFeature`, and `SchedulingModule` were deleted in Task 5. This checkpoint confirms the filesystem control surface (Tasks 12–17) is the only remaining scheduling interface.

- [ ] **Step 1: Confirm removal**

Run: `grep -rn "ScheduleCreateTool\|ScheduleListTool\|ScheduleDeleteTool\|SchedulingToolFeature\|AddScheduling\|domain__scheduling" Domain Agent`
Expected: no results (config whitelist patterns are cleaned in Task 19).

- [ ] **Step 2: Confirm the store no longer registers in the Agent**

Run: `grep -rn "RedisScheduleStore\|IScheduleStore" Agent`
Expected: no results — the store is now consumed only by `McpServerScheduling` (it physically remains in `Infrastructure`, which the new server references).

- [ ] **Step 3: Run the scheduling unit suite**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Scheduling"`
Expected: PASS (new tests only; the deleted tools' tests are gone).

- [ ] **Step 4: Commit** (only if a stray reference needed fixing)

```bash
git add -A
git commit -m "chore(scheduling): confirm domain tool removal"
```

---

# Phase 5 — Config cutover, integration, prompt

## Task 19: Wire the server into config + compose

**Files:**
- Modify: `Agent/appsettings.json` (add `scheduling` channel endpoint; add the server to scheduling-capable agents' `mcpServerEndpoints`)
- Modify: `McpServerScheduling/appsettings.json` (agents list + default deliverTo)
- Modify: `DockerCompose/docker-compose.yml` (new `mcp-scheduling` service)
- Modify: `CLAUDE.md` launch command lists (add `mcp-scheduling`)

No automated test; verified by `docker compose config` and a smoke run.

- [ ] **Step 1: Add the channel endpoint and mount**

In `Agent/appsettings.json`, append to `channelEndpoints`:

```json
        { "channelId": "scheduling", "endpoint": "http://mcp-scheduling:8080/mcp" }
```

Remove the now-obsolete sibling `"DefaultScheduleChannelId": "signalr"` key. Add `http://mcp-scheduling:8080/mcp` to the `mcpServerEndpoints` of every agent whose `enabledFeatures` included `"scheduling"` (e.g. `jonas`), and drop `"scheduling"` from `enabledFeatures` and `"domain__scheduling*"` from `whitelistPatterns` (those domain tools no longer exist; the agent now reaches scheduling via the `filesystem://schedules` mount).

- [ ] **Step 2: Populate the server's agent list**

In `McpServerScheduling/appsettings.json`, set `Agents` to the id/name/description of each schedulable agent and `DefaultDeliverTo` to `["signalr"]`. Keep the values aligned with the Agent's `Agents` config.

- [ ] **Step 3: Add the compose service**

In `DockerCompose/docker-compose.yml`, add (mirroring `mcp-channel-servicebus`), with the user-secrets mount and Redis dependency:

```yaml
  mcp-scheduling:
    build:
      context: ..
      dockerfile: McpServerScheduling/Dockerfile
    environment:
      - RedisConnectionString=redis:6379
    depends_on:
      - redis
    restart: unless-stopped
```

- [ ] **Step 4: Validate compose + build**

Run: `docker compose -f DockerCompose/docker-compose.yml config >/dev/null && echo OK`
Expected: `OK`.

- [ ] **Step 5: Commit**

```bash
git add Agent/appsettings.json McpServerScheduling/appsettings.json DockerCompose/docker-compose.yml CLAUDE.md
git commit -m "chore(scheduling): wire mcp-scheduling channel + filesystem into config/compose"
```

## Task 20: Integration test — discovery + create/read round-trip

**Files:**
- Create: `Tests/Integration/McpServerTests/McpSchedulingServerTests.cs`

Mirror `Tests/Integration/McpServerTests/McpVaultServerTests.cs`: start the server in-memory, connect an `McpClient`, run discovery, and exercise create → glob → read through `McpFileSystemBackend`. This test is `Category!=E2E` but needs Redis; mark it consistently with the existing Vault/Library server tests (they share the Redis/Docker baseline noted in Conventions).

- [ ] **Step 1: Write the test** (mirror the existing Vault server integration test's harness; assert that a `fs_create` of `/jonas/itest-news/schedule.json` then `fs_glob /jonas/*` returns the new schedule, and `fs_read` returns the prompt). Use the same fixture pattern as `McpVaultServerTests`.

- [ ] **Step 2: Run it**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpSchedulingServerTests"`
Expected: PASS where Redis is available; otherwise the same `DockerUnavailableException` skip/fail as the sibling server tests (baseline, not a regression).

- [ ] **Step 3: Commit**

```bash
git add Tests/Integration/McpServerTests/McpSchedulingServerTests.cs
git commit -m "test(scheduling): integration test for discovery + create/read round-trip"
```

## Task 21: Prompt cutover

**Files:**
- Locate the scheduling prompt guidance: `grep -rln "schedule" Domain/Prompts`
- Modify the relevant prompt(s) to teach the filesystem idiom.

- [ ] **Step 1: Find current scheduling prompt text**

Run: `grep -rln "schedule\|Scheduling\|cron" Domain/Prompts Agent`
Identify where the old `domain__scheduling__*` tools were described.

- [ ] **Step 2: Rewrite to the filesystem idiom**

Replace tool-centric guidance with: glob `/schedules/*` to pick an agent → read `agent_info.json` → `fs_create /schedules/<agent>/<descriptive-unique-id>/schedule.json` with `{prompt, cron|runAt, userId?, deliverTo?}` → `fs_edit` to change, `fs_move` to reassign, `fs_delete` to remove, `fs_exec run_now.sh` to fire now → read `status.json` for next/last run. Note ids must be descriptive and unique; cron XOR runAt; times are UTC.

- [ ] **Step 3: Build + commit**

Run: `dotnet build agent.sln`

```bash
git add Domain/Prompts
git commit -m "docs(scheduling): rewrite prompt for filesystem idiom"
```

## Task 22: Full verification sweep

- [ ] **Step 1:** `dotnet build agent.sln` → succeeds.
- [ ] **Step 2:** `dotnet test Tests/Tests.csproj --filter "Category!=E2E&FullyQualifiedName~Scheduling"` → new tests PASS.
- [ ] **Step 3:** `dotnet test Tests/Tests.csproj --filter "Category!=E2E&FullyQualifiedName~ChatMonitor"` → PASS (fan-out + metrics).
- [ ] **Step 4:** `grep -rn "IScheduleAgentFactory\|ScheduleExecutor\|ScheduleDispatcher\|SchedulingToolFeature\|domain__scheduling" Domain Agent Infrastructure` → no results (all removed).
- [ ] **Step 5: Commit** any final cleanup, then proceed to branch finishing per `superpowers:finishing-a-development-branch`.

---

## Self-Review notes (for the executor)

- **Schedule store one-shot TTL:** `RedisScheduleStore.CreateAsync` sets a 1-hour `KeyExpire` for one-shot (`RunAt`) schedules. The dispatcher deletes one-shots on fire, so the TTL is just a safety net — no change needed.
- **`Size` is a string** in `FsCreateResult` (matches the existing contract) — don't "fix" it to an int.
- **`fs_search` ignores most params** in this VFS (no regex/context). That's acceptable: the wrapper still accepts the full signature for protocol uniformity; the engine does a simple contains-match.
- **Strict payload validation:** every `FsResultContract.ToNode(...)` DTO must exactly match the existing `Domain/DTOs/FileSystem` records. If a build/serialization test reports an unmapped member, you added a field that isn't in the DTO — don't; reuse the DTO as-is.
- **`TextEdit` location:** confirm with `grep -rn "record TextEdit" Domain` before relying on `Domain.DTOs.TextEdit` in `EditAsync`/`FsEditTool`.
