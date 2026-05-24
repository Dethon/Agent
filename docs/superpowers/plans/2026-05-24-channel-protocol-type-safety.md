# Type-Safe Channel Protocol Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the string-convention coupling in the agent↔channel-server protocol with shared typed DTOs and a single `ChannelProtocol` source of truth.

**Architecture:** Add `Domain/DTOs/Channel/ChannelProtocol.cs` (method/tool-name constants, `JsonSerializerOptions`, `ToArguments`/`Deserialize` helpers) plus two notification records and one params record. The agent serializes typed param DTOs into tool arguments and deserializes notifications into typed records; every channel server (SignalR, Telegram, ServiceBus, Scheduling) constructs the same shared records and exposes its tools by the shared name constants. Nested lists (`requests`, `agents`) become native typed-array parameters, eliminating double-serialized JSON-string blobs and the divergent Telegram approval record.

**Tech Stack:** .NET 10 / C# 14, `System.Text.Json`, `ModelContextProtocol` 1.2.0, Shouldly + Moq + xUnit (xUnit imported via global using), Testcontainers (`redis/redis-stack`).

**Spec:** `docs/superpowers/specs/2026-05-24-channel-protocol-type-safety-design.md`

**Conventions (apply to every task):**
- TDD: write the failing test first, run it, see it fail, then implement. Pure refactor tasks (no behavior change) are explicitly marked REFACTOR and use the existing suite + build as the safety net.
- Shouldly assertions (not `Assert.*`). Test naming `{Method}_{Scenario}_{ExpectedResult}`.
- **No trailing newline in any `.cs` file** (including tests).
- Build target is `agent.sln` with **0 warnings, 0 errors**.
- `Tests` has `global using Xunit;` — do **not** add `using Xunit;` to new test files.
- The repo's Docker-dependent integration tests fail in CI-less environments with `DockerUnavailableException` (~148 baseline `Category!=E2E` failures). "Tests pass" means **no new failures beyond that baseline**.

---

## File Structure

**New (Domain/DTOs/Channel/):**
- `ChannelProtocol.cs` — static: name constants, `SerializerOptions`, `ToArguments<T>`, `Deserialize<T>`.
- `ChannelMessageNotification.cs` — wire record for `channel/message`.
- `ChannelCancelNotification.cs` — wire record for `channel/cancel`.
- `RegisterAgentsParams.cs` — one-field params record for `register_agents`.

**Modified:**
- `Domain/DTOs/Channel/RequestApprovalParams.cs` — `Requests` `string` → `IReadOnlyList<ToolApprovalRequest>`.
- `Infrastructure/Clients/Channels/McpChannelConnection.cs` — typed handlers, `ToArguments` callers, name constants, optional logger.
- `Agent/Modules/InjectorModule.cs` — supply logger to each connection.
- `McpChannel{SignalR,Telegram,ServiceBus}/Services/ChannelNotificationEmitter.cs` — shared records + constants.
- `McpServerScheduling/Services/ScheduleNotificationEmitter.cs` — drop `SchedulePayload`, use shared record.
- `McpServerScheduling/Services/ScheduleFirePlanner.cs` — `FirePlan.Payload` retype.
- `McpChannel{SignalR,Telegram,ServiceBus}/McpTools/SendReplyTool.cs`, `McpServerScheduling/McpTools/SendReplyTool.cs` — name constant.
- `McpChannelSignalR/McpTools/CreateConversationTool.cs` — name constant.
- `McpChannel{SignalR,Telegram,ServiceBus}/McpTools/RequestApprovalTool.cs`, `McpServerScheduling/McpTools/RequestApprovalTool.cs` — typed `requests` + name constant.
- `McpChannelSignalR/Services/ApprovalService.cs` — typed `Requests`, delete `DeserializeRequests`.
- `McpChannelSignalR/McpTools/RegisterAgentsTool.cs` — typed `agents` + name constant.

**Tests:**
- New: `Tests/Unit/Domain/Channel/ChannelProtocolTests.cs`, `Tests/Unit/Domain/Channel/ChannelProtocolDtoTests.cs`.
- Modified: `Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs`, `Tests/Unit/McpServerScheduling/ScheduleNotificationPayloadTests.cs`, `Tests/Unit/McpChannelSignalR/RegisterAgentsToolTests.cs`.

---

## Task 1: `ChannelProtocol` foundation (Domain)

**Files:**
- Create: `Domain/DTOs/Channel/ChannelProtocol.cs`
- Test: `Tests/Unit/Domain/Channel/ChannelProtocolTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Domain/Channel/ChannelProtocolTests.cs`:

```csharp
using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

public class ChannelProtocolTests
{
    [Fact]
    public void ToArguments_WithSendReplyParams_ProducesCamelCaseKeysAndStringEnum()
    {
        var p = new SendReplyParams
        {
            ConversationId = "c1",
            Content = "hi",
            ContentType = ReplyContentType.Text,
            IsComplete = true,
            MessageId = "m1"
        };

        var args = ChannelProtocol.ToArguments(p);

        args.Keys.OrderBy(k => k)
            .ShouldBe(["content", "contentType", "conversationId", "isComplete", "messageId"]);
        JsonSerializer.Serialize(args["contentType"]).ShouldBe("\"Text\"");
    }

    [Fact]
    public void Deserialize_WithCamelCasePayload_ReadsTypedDto()
    {
        var element = JsonSerializer.Deserialize<JsonElement>(
            """{"conversationId":"c1","content":"hi","contentType":"Reasoning","isComplete":false,"messageId":null}""");

        var p = ChannelProtocol.Deserialize<SendReplyParams>(element);

        p.ShouldNotBeNull();
        p!.ConversationId.ShouldBe("c1");
        p.ContentType.ShouldBe(ReplyContentType.Reasoning);
        p.IsComplete.ShouldBeFalse();
        p.MessageId.ShouldBeNull();
    }

    [Fact]
    public void NameConstants_MatchWireProtocol()
    {
        ChannelProtocol.MessageNotification.ShouldBe("notifications/channel/message");
        ChannelProtocol.CancelNotification.ShouldBe("notifications/channel/cancel");
        ChannelProtocol.SendReplyTool.ShouldBe("send_reply");
        ChannelProtocol.RequestApprovalTool.ShouldBe("request_approval");
        ChannelProtocol.CreateConversationTool.ShouldBe("create_conversation");
        ChannelProtocol.RegisterAgentsTool.ShouldBe("register_agents");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelProtocolTests"`
Expected: FAIL — build error, `ChannelProtocol` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Domain/DTOs/Channel/ChannelProtocol.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public static class ChannelProtocol
{
    public const string MessageNotification = "notifications/channel/message";
    public const string CancelNotification = "notifications/channel/cancel";
    public const string SendReplyTool = "send_reply";
    public const string RequestApprovalTool = "request_approval";
    public const string CreateConversationTool = "create_conversation";
    public const string RegisterAgentsTool = "register_agents";

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyDictionary<string, object?> ToArguments<T>(T value)
    {
        using var document = JsonSerializer.SerializeToDocument(value, SerializerOptions);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
    }

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(SerializerOptions);
}
```

Note: `JsonSerializerDefaults.Web` gives camelCase naming + case-insensitive reads; `JsonStringEnumConverter` keeps enums on the wire as their names (`"Text"`, `"Request"`), matching the current protocol. `.Clone()` detaches each value so it outlives the disposed document.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelProtocolTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Channel/ChannelProtocol.cs Tests/Unit/Domain/Channel/ChannelProtocolTests.cs
git commit -m "feat(channel): add ChannelProtocol shared serialization surface"
```

---

## Task 2: Shared protocol DTOs (notification records + register-agents params)

**Files:**
- Create: `Domain/DTOs/Channel/ChannelMessageNotification.cs`, `Domain/DTOs/Channel/ChannelCancelNotification.cs`, `Domain/DTOs/Channel/RegisterAgentsParams.cs`
- Test: `Tests/Unit/Domain/Channel/ChannelProtocolDtoTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Domain/Channel/ChannelProtocolDtoTests.cs`:

```csharp
using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

public class ChannelProtocolDtoTests
{
    [Fact]
    public void ChannelMessageNotification_RoundTripsThroughProtocol()
    {
        var original = new ChannelMessageNotification
        {
            ConversationId = "c1",
            Sender = "scheduler",
            Content = "run",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-1")],
            Origin = new MessageOrigin("schedule", "morning-news"),
            Timestamp = DateTimeOffset.UnixEpoch
        };

        var element = JsonSerializer.SerializeToElement(original, ChannelProtocol.SerializerOptions);
        var copy = ChannelProtocol.Deserialize<ChannelMessageNotification>(element);

        copy.ShouldNotBeNull();
        copy!.AgentId.ShouldBe("jonas");
        copy.ReplyTo!.Count.ShouldBe(2);
        copy.ReplyTo[0].ChannelId.ShouldBe("signalr");
        copy.ReplyTo[0].ConversationId.ShouldBeNull();
        copy.Origin!.Kind.ShouldBe("schedule");
        copy.Origin.ScheduleId.ShouldBe("morning-news");
        element.GetProperty("replyTo")[1].GetProperty("conversationId").GetString().ShouldBe("t-1");
    }

    [Fact]
    public void ChannelCancelNotification_RoundTripsThroughProtocol()
    {
        var original = new ChannelCancelNotification
        {
            ConversationId = "c1",
            AgentId = "jonas",
            Timestamp = DateTimeOffset.UnixEpoch
        };

        var element = JsonSerializer.SerializeToElement(original, ChannelProtocol.SerializerOptions);
        var copy = ChannelProtocol.Deserialize<ChannelCancelNotification>(element);

        copy.ShouldNotBeNull();
        copy!.ConversationId.ShouldBe("c1");
        copy.AgentId.ShouldBe("jonas");
    }

    [Fact]
    public void ToArguments_WithRegisterAgentsParams_ProducesAgentsArray()
    {
        var p = new RegisterAgentsParams
        {
            Agents = [new AgentCatalogEntry("jack", "Jack", "Downloads")]
        };

        var args = ChannelProtocol.ToArguments(p);

        args.Keys.ShouldBe(["agents"]);
        JsonSerializer.Serialize(args["agents"]).ShouldContain("\"id\":\"jack\"");
    }

    [Fact]
    public void ToArguments_WithCreateConversationParams_ProducesExpectedKeys()
    {
        var p = new CreateConversationParams
        {
            AgentId = "jonas",
            TopicName = "Topic",
            Sender = "user"
        };

        var args = ChannelProtocol.ToArguments(p);

        args.Keys.OrderBy(k => k).ShouldBe(["agentId", "sender", "topicName"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelProtocolDtoTests"`
Expected: FAIL — build error, `ChannelMessageNotification` / `ChannelCancelNotification` / `RegisterAgentsParams` do not exist.

- [ ] **Step 3: Write the implementations**

Create `Domain/DTOs/Channel/ChannelMessageNotification.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ChannelMessageNotification
{
    public required string ConversationId { get; init; }
    public required string Sender { get; init; }
    public required string Content { get; init; }
    public string? AgentId { get; init; }
    public IReadOnlyList<ReplyTarget>? ReplyTo { get; init; }
    public MessageOrigin? Origin { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

Create `Domain/DTOs/Channel/ChannelCancelNotification.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ChannelCancelNotification
{
    public required string ConversationId { get; init; }
    public string? AgentId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

Create `Domain/DTOs/Channel/RegisterAgentsParams.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record RegisterAgentsParams
{
    public required IReadOnlyList<AgentCatalogEntry> Agents { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelProtocolDtoTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Channel/ChannelMessageNotification.cs Domain/DTOs/Channel/ChannelCancelNotification.cs Domain/DTOs/Channel/RegisterAgentsParams.cs Tests/Unit/Domain/Channel/ChannelProtocolDtoTests.cs
git commit -m "feat(channel): add shared notification records and RegisterAgentsParams"
```

---

## Task 3: Agent consumer — typed notification handlers

**Files:**
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs`
- Modify: `Agent/Modules/InjectorModule.cs:51-58`
- Test: `Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs`

This task switches the two notification handlers from hand-parsing `JsonElement` to `ChannelProtocol.Deserialize`, adds an optional logger so malformed payloads are logged-and-skipped instead of throwing, and wires the logger through DI. The tool-caller methods (`SendReplyAsync`, etc.) are untouched here.

- [ ] **Step 1: Write the failing test**

Add this test to `Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs` (keep the two existing tests):

```csharp
    [Fact]
    public async Task HandleChannelMessageNotification_WithMalformedPayload_WritesNothing()
    {
        var conn = new McpChannelConnection("signalr");
        conn.HandleChannelMessageNotification(Json("""{"sender":"user"}"""));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var read = async () =>
        {
            await foreach (var _ in conn.Messages.WithCancellation(cts.Token))
            {
                return;
            }
        };

        await Should.ThrowAsync<OperationCanceledException>(read);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelConnectionParsingTests"`
Expected: FAIL — current `HandleChannelMessageNotification` calls `payload.GetProperty("conversationId")`, which throws `KeyNotFoundException` (the assertion never gets the chance to see "nothing written").

- [ ] **Step 3: Update `McpChannelConnection`**

In `Infrastructure/Clients/Channels/McpChannelConnection.cs`:

Add the logging using at the top (after the existing usings):

```csharp
using Microsoft.Extensions.Logging;
```

Change the class declaration to accept an optional logger:

```csharp
public sealed class McpChannelConnection(string channelId, ILogger<McpChannelConnection>? logger = null)
    : IChannelConnection, IMcpChannelConnection, IAsyncDisposable
```

Delete the two notification-method constants (the `ChannelMessageNotification` / `ChannelCancelNotification` `const string` lines) — they collide with the new record type names and are replaced by `ChannelProtocol`. Keep `CancelCommandContent` and `SystemSender`:

```csharp
    private const string CancelCommandContent = "/cancel";
    private const string SystemSender = "system";
```

In `ConnectAsync`, change the two `RegisterNotificationHandler` calls to use the protocol constants:

```csharp
        _client.RegisterNotificationHandler(
            ChannelProtocol.MessageNotification,
            (notification, _) =>
            {
                if (notification.Params is { } paramsNode)
                {
                    var element = JsonSerializer.Deserialize<JsonElement>(paramsNode.ToJsonString());
                    HandleChannelMessageNotification(element);
                }

                return ValueTask.CompletedTask;
            });

        _client.RegisterNotificationHandler(
            ChannelProtocol.CancelNotification,
            (notification, _) =>
            {
                if (notification.Params is { } paramsNode)
                {
                    var element = JsonSerializer.Deserialize<JsonElement>(paramsNode.ToJsonString());
                    HandleChannelCancelNotification(element);
                }

                return ValueTask.CompletedTask;
            });
```

Replace the whole body of `HandleChannelMessageNotification`:

```csharp
    public void HandleChannelMessageNotification(JsonElement payload)
    {
        ChannelMessageNotification? notification;
        try
        {
            notification = ChannelProtocol.Deserialize<ChannelMessageNotification>(payload);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Discarding malformed channel/message notification on {ChannelId}", ChannelId);
            return;
        }

        if (notification is null)
        {
            return;
        }

        var message = new ChannelMessage
        {
            ConversationId = notification.ConversationId,
            Content = notification.Content,
            Sender = notification.Sender,
            ChannelId = ChannelId,
            AgentId = notification.AgentId,
            ReplyTo = notification.ReplyTo,
            Origin = notification.Origin
        };

        _messageChannel.Writer.TryWrite(message);
    }
```

Replace the whole body of `HandleChannelCancelNotification`:

```csharp
    public void HandleChannelCancelNotification(JsonElement payload)
    {
        ChannelCancelNotification? notification;
        try
        {
            notification = ChannelProtocol.Deserialize<ChannelCancelNotification>(payload);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Discarding malformed channel/cancel notification on {ChannelId}", ChannelId);
            return;
        }

        if (notification is null)
        {
            return;
        }

        var message = new ChannelMessage
        {
            ConversationId = notification.ConversationId,
            Content = CancelCommandContent,
            Sender = SystemSender,
            ChannelId = ChannelId,
            AgentId = notification.AgentId
        };

        _messageChannel.Writer.TryWrite(message);
    }
```

- [ ] **Step 4: Wire the logger in DI**

In `Agent/Modules/InjectorModule.cs`, replace the eager connection construction (currently `var channelConnections = settings.ChannelEndpoints.Select(ep => new McpChannelConnection(ep.ChannelId)).ToList();` followed by the `foreach (var conn in channelConnections)` registration loop) with per-endpoint factory registrations that resolve the logger:

```csharp
            foreach (var endpoint in settings.ChannelEndpoints)
            {
                var channelId = endpoint.ChannelId;
                services = services.AddSingleton<IChannelConnection>(sp =>
                    new McpChannelConnection(channelId, sp.GetService<ILogger<McpChannelConnection>>()));
            }
```

If `Microsoft.Extensions.Logging` is not already imported in this file, add `using Microsoft.Extensions.Logging;`. The later registrations (`AddSingleton<IReadOnlyList<IChannelConnection>>(...)` and the `ChannelConnectionHost` registration that calls `sp.GetServices<IChannelConnection>()`) are unchanged — each factory-registered singleton is still returned by `GetServices`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelConnectionParsingTests|FullyQualifiedName~McpChannelConnectionTests"`
Expected: PASS — the new malformed test passes, and the three existing `McpChannelConnectionTests` (camelCase anonymous payloads) still pass through the deserialize path.

- [ ] **Step 6: Build the solution**

Run: `dotnet build agent.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add Infrastructure/Clients/Channels/McpChannelConnection.cs Agent/Modules/InjectorModule.cs Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs
git commit -m "feat(channel): deserialize notifications into shared records on the agent"
```

---

## Task 4: Notification publishers — shared records

**Files:**
- Modify: `McpChannelSignalR/Services/ChannelNotificationEmitter.cs`
- Modify: `McpChannelTelegram/Services/ChannelNotificationEmitter.cs`
- Modify: `McpChannelServiceBus/Services/ChannelNotificationEmitter.cs`
- Modify: `McpServerScheduling/Services/ScheduleNotificationEmitter.cs`
- Modify: `McpServerScheduling/Services/ScheduleFirePlanner.cs`
- Test: `Tests/Unit/McpServerScheduling/ScheduleNotificationPayloadTests.cs`

The wire shape is byte-identical (camelCase). The Scheduling emitter is the TDD anchor (it has a unit test); the three transport emitters are mechanical record substitutions verified by build + the consumer tests from Task 3.

- [ ] **Step 1: Update the failing test (Scheduling anchor)**

Replace the single test method in `Tests/Unit/McpServerScheduling/ScheduleNotificationPayloadTests.cs` with this (and update usings to include `Domain.DTOs.Channel`, which is already imported):

```csharp
using System.Text.Json;
using Domain.DTOs.Channel;
using McpServerScheduling.Services;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleNotificationPayloadTests
{
    [Fact]
    public void BuildPayload_WithReplyToAndOrigin_ProducesChannelMessageNotification()
    {
        ChannelMessageNotification payload = ScheduleNotificationEmitter.BuildPayload(
            conversationId: "fire-1",
            sender: "scheduler",
            content: "do it",
            agentId: "jonas",
            replyTo: [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-1")],
            origin: new MessageOrigin("schedule", "morning-news"));

        payload.AgentId.ShouldBe("jonas");
        payload.ReplyTo!.Count.ShouldBe(2);
        payload.Origin!.ScheduleId.ShouldBe("morning-news");

        var json = JsonSerializer.Serialize(payload, ChannelProtocol.SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("replyTo")[1].GetProperty("conversationId").GetString().ShouldBe("t-1");
        root.GetProperty("origin").GetProperty("kind").GetString().ShouldBe("schedule");
    }
}
```

(This file keeps its explicit `using Xunit;`, matching its current style.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleNotificationPayloadTests"`
Expected: FAIL — build error, `BuildPayload` returns `SchedulePayload`, which cannot be assigned to `ChannelMessageNotification`.

- [ ] **Step 3: Update the Scheduling emitter**

In `McpServerScheduling/Services/ScheduleNotificationEmitter.cs`, delete the `SchedulePayload` record and retype `BuildPayload`/`EmitAsync`:

```csharp
using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerScheduling.Services;

public sealed class ScheduleNotificationEmitter(ILogger<ScheduleNotificationEmitter> logger)
{
    private readonly ConcurrentDictionary<string, McpServer> _activeSessions = new();

    public void RegisterSession(string sessionId, McpServer server)
    {
        _activeSessions[sessionId] = server;
        logger.LogInformation("MCP session registered: {SessionId}", sessionId);
    }

    public void UnregisterSession(string sessionId)
    {
        _activeSessions.TryRemove(sessionId, out _);
        logger.LogInformation("MCP session unregistered: {SessionId}", sessionId);
    }

    public bool HasActiveSessions => !_activeSessions.IsEmpty;

    public static ChannelMessageNotification BuildPayload(
        string conversationId, string sender, string content, string agentId,
        IReadOnlyList<ReplyTarget> replyTo, MessageOrigin origin) =>
        new()
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            ReplyTo = replyTo,
            Origin = origin,
            Timestamp = DateTimeOffset.UtcNow
        };

    public async Task EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync(ChannelProtocol.MessageNotification, payload, cancellationToken: ct);
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

- [ ] **Step 4: Retype `FirePlan`**

In `McpServerScheduling/Services/ScheduleFirePlanner.cs`, change the record's `Payload` type (the `Plan` body is unchanged — `payload` is `var`):

```csharp
public sealed record FirePlan(ChannelMessageNotification Payload, DateTime? NextRunAt, bool DeleteAfterFire);
```

- [ ] **Step 5: Update the three transport emitters**

In `McpChannelSignalR/Services/ChannelNotificationEmitter.cs`, add `using Domain.DTOs.Channel;` and replace the anonymous-object payloads. The message payload becomes:

```csharp
        var payload = new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        };
```

and its `SendNotificationAsync` first argument becomes `ChannelProtocol.MessageNotification`. The cancel payload becomes:

```csharp
        var payload = new ChannelCancelNotification
        {
            ConversationId = conversationId,
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        };
```

with its `SendNotificationAsync` first argument `ChannelProtocol.CancelNotification`.

In `McpChannelTelegram/Services/ChannelNotificationEmitter.cs` and `McpChannelServiceBus/Services/ChannelNotificationEmitter.cs` (both have only the message emitter), add `using Domain.DTOs.Channel;` and apply the same `ChannelMessageNotification` replacement with `ChannelProtocol.MessageNotification` as the first `SendNotificationAsync` argument.

- [ ] **Step 6: Run test + build**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleNotificationPayloadTests"`
Expected: PASS.

Run: `dotnet build agent.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add McpChannelSignalR/Services/ChannelNotificationEmitter.cs McpChannelTelegram/Services/ChannelNotificationEmitter.cs McpChannelServiceBus/Services/ChannelNotificationEmitter.cs McpServerScheduling/Services/ScheduleNotificationEmitter.cs McpServerScheduling/Services/ScheduleFirePlanner.cs Tests/Unit/McpServerScheduling/ScheduleNotificationPayloadTests.cs
git commit -m "feat(channel): emit notifications as shared records from every channel server"
```

---

## Task 5: `send_reply` + `create_conversation` through `ChannelProtocol` (REFACTOR)

**Files:**
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs` (`SendReplyAsync`, `CreateConversationAsync`)
- Modify: `McpChannelSignalR/McpTools/SendReplyTool.cs`, `McpChannelTelegram/McpTools/SendReplyTool.cs`, `McpChannelServiceBus/McpTools/SendReplyTool.cs`, `McpServerScheduling/McpTools/SendReplyTool.cs`
- Modify: `McpChannelSignalR/McpTools/CreateConversationTool.cs`

No behavior change (the produced wire is identical: camelCase keys, `contentType` as `"Text"`). **No new test** — the contract keys are already pinned by `ChannelProtocolTests`/`ChannelProtocolDtoTests` (Task 1/2), and `McpChannelConnectionTests` plus the build are the regression net. Mark RED N/A for this task.

- [ ] **Step 1: Route the agent callers through `ToArguments`**

In `Infrastructure/Clients/Channels/McpChannelConnection.cs`, replace the body of `SendReplyAsync` (the `CallToolAsync` invocation) with:

```csharp
        EnsureConnected();
        await _client!.CallToolAsync(
            ChannelProtocol.SendReplyTool,
            ChannelProtocol.ToArguments(new SendReplyParams
            {
                ConversationId = conversationId,
                Content = content,
                ContentType = contentType,
                IsComplete = isComplete,
                MessageId = messageId
            }),
            cancellationToken: ct);
```

In `CreateConversationAsync`, change the tool-presence probe and the call:

```csharp
            if (tools.All(t => t.Name != ChannelProtocol.CreateConversationTool))
            {
                return null;
            }

            var result = await _client.CallToolAsync(
                ChannelProtocol.CreateConversationTool,
                ChannelProtocol.ToArguments(new CreateConversationParams
                {
                    AgentId = agentId,
                    TopicName = topicName,
                    Sender = sender
                }),
                cancellationToken: ct);
```

- [ ] **Step 2: Use the name constant on the tool attributes**

In each of `McpChannelSignalR/McpTools/SendReplyTool.cs`, `McpChannelTelegram/McpTools/SendReplyTool.cs`, `McpChannelServiceBus/McpTools/SendReplyTool.cs`, and `McpServerScheduling/McpTools/SendReplyTool.cs`, change the attribute to:

```csharp
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
```

In `McpServerScheduling/McpTools/SendReplyTool.cs` add `using Domain.DTOs.Channel;` (it currently imports only `Domain.DTOs`). The other three already import `Domain.DTOs.Channel`.

In `McpChannelSignalR/McpTools/CreateConversationTool.cs`, change the attribute to:

```csharp
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
```

(`Domain.DTOs.Channel` is already imported there.)

- [ ] **Step 3: Build and run the connection regression tests**

Run: `dotnet build agent.sln`
Expected: 0 warnings, 0 errors.

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelConnectionTests|FullyQualifiedName~ChannelProtocol"`
Expected: PASS (no regressions).

- [ ] **Step 4: Commit**

```bash
git add Infrastructure/Clients/Channels/McpChannelConnection.cs McpChannelSignalR/McpTools/SendReplyTool.cs McpChannelTelegram/McpTools/SendReplyTool.cs McpChannelServiceBus/McpTools/SendReplyTool.cs McpServerScheduling/McpTools/SendReplyTool.cs McpChannelSignalR/McpTools/CreateConversationTool.cs
git commit -m "refactor(channel): build send_reply/create_conversation args via ChannelProtocol"
```

---

## Task 6: `request_approval` native-typed list (atomic)

**Files:**
- Modify: `Domain/DTOs/Channel/RequestApprovalParams.cs`
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs` (`RequestApprovalAsync`, `NotifyAutoApprovedAsync`)
- Modify: `McpChannelSignalR/McpTools/RequestApprovalTool.cs`, `McpChannelTelegram/McpTools/RequestApprovalTool.cs`, `McpChannelServiceBus/McpTools/RequestApprovalTool.cs`, `McpServerScheduling/McpTools/RequestApprovalTool.cs`
- Modify: `McpChannelSignalR/Services/ApprovalService.cs`
- Test: `Tests/Unit/Domain/Channel/ChannelProtocolDtoTests.cs`

Retyping `RequestApprovalParams.Requests` breaks every reference at once; all the changes below land together so the solution compiles and the test goes green.

- [ ] **Step 1: Write the failing test**

Add to `Tests/Unit/Domain/Channel/ChannelProtocolDtoTests.cs` (the `using Domain.DTOs;` import is already present):

```csharp
    [Fact]
    public void ToArguments_WithRequestApprovalParams_SerializesRequestsAsArrayPreservingMessageId()
    {
        var p = new RequestApprovalParams
        {
            ConversationId = "c1",
            Mode = ApprovalMode.Request,
            Requests = [new ToolApprovalRequest("m1", "mcp__x__do", new Dictionary<string, object?> { ["k"] = "v" })]
        };

        var args = ChannelProtocol.ToArguments(p);
        var requests = (JsonElement)args["requests"]!;

        requests.ValueKind.ShouldBe(JsonValueKind.Array);
        requests[0].GetProperty("messageId").GetString().ShouldBe("m1");
        requests[0].GetProperty("toolName").GetString().ShouldBe("mcp__x__do");
        JsonSerializer.Serialize(args["mode"]).ShouldBe("\"Request\"");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelProtocolDtoTests"`
Expected: FAIL — build error: a `ToolApprovalRequest` list cannot be assigned to `RequestApprovalParams.Requests` (still `string`).

- [ ] **Step 3: Retype the shared DTO**

In `Domain/DTOs/Channel/RequestApprovalParams.cs`:

```csharp
using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record RequestApprovalParams
{
    public required string ConversationId { get; init; }
    public required ApprovalMode Mode { get; init; }
    public required IReadOnlyList<ToolApprovalRequest> Requests { get; init; }
}
```

(`ToolApprovalRequest` lives in `Domain.DTOs`; `ApprovalMode` also in `Domain.DTOs`.)

- [ ] **Step 4: Update the agent callers**

In `Infrastructure/Clients/Channels/McpChannelConnection.cs`, replace the `RequestApprovalAsync` `CallToolAsync` invocation:

```csharp
        EnsureConnected();
        var result = await _client!.CallToolAsync(
            ChannelProtocol.RequestApprovalTool,
            ChannelProtocol.ToArguments(new RequestApprovalParams
            {
                ConversationId = conversationId,
                Mode = ApprovalMode.Request,
                Requests = requests
            }),
            cancellationToken: ct);
```

and the `NotifyAutoApprovedAsync` invocation:

```csharp
        EnsureConnected();
        await _client!.CallToolAsync(
            ChannelProtocol.RequestApprovalTool,
            ChannelProtocol.ToArguments(new RequestApprovalParams
            {
                ConversationId = conversationId,
                Mode = ApprovalMode.Notify,
                Requests = requests
            }),
            cancellationToken: ct);
```

- [ ] **Step 5: Update the four `request_approval` tools**

`McpChannelSignalR/McpTools/RequestApprovalTool.cs` — change the parameter and attribute:

```csharp
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval from user or notify about auto-approved tools")]
    public static async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };
        // ...rest unchanged...
```

`McpChannelTelegram/McpTools/RequestApprovalTool.cs` — change the parameter and attribute, delete the local `ToolRequest` record, read `p.Requests` directly, and retype `FormatApprovalMessage`. The full file becomes:

```csharp
using System.ComponentModel;
using System.Text;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    private static readonly TimeSpan _approvalTimeout = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval from user or notify about auto-approved tools")]
    public static async Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        var registry = services.GetRequiredService<BotRegistry>();
        var router = services.GetRequiredService<ApprovalCallbackRouter>();
        var (chatId, threadId) = ParseConversationId(p.ConversationId);
        var botClient = registry.GetBotForChat(chatId)
                        ?? throw new InvalidOperationException($"No bot registered for chat {chatId}");

        if (p.Mode == ApprovalMode.Notify)
        {
            var toolNames = p.Requests.Select(r => r.ToolName.Split("__").Last());
            var message = $"✅ Auto-approved: {string.Join(", ", toolNames)}";

            await botClient.SendMessage(
                chatId,
                message,
                messageThreadId: threadId,
                cancellationToken: CancellationToken.None);

            return "notified";
        }

        var (approvalId, resultTask) = router.RegisterApproval(_approvalTimeout, CancellationToken.None);
        var keyboard = ApprovalCallbackRouter.CreateApprovalKeyboard(approvalId);

        var approvalMessage = FormatApprovalMessage(p.Requests);

        await botClient.SendMessage(
            chatId,
            approvalMessage,
            ParseMode.Html,
            replyMarkup: keyboard,
            messageThreadId: threadId,
            cancellationToken: CancellationToken.None);

        return await resultTask;
    }

    private static string FormatApprovalMessage(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();
        var toolNames = string.Join(", ", requests.Select(r => r.ToolName.Split("__").Last()));
        sb.AppendLine($"<b>🔧 Approval Required:</b> <code>{HtmlEncode(toolNames)}</code>");

        foreach (var request in requests)
        {
            if (request.Arguments.Count == 0)
            {
                continue;
            }

            var details = new StringBuilder();
            foreach (var (key, value) in request.Arguments)
            {
                var formatted = value?.ToString()?.Replace("\n", " ") ?? "null";
                if (formatted.Length > 100)
                {
                    formatted = formatted[..100] + "...";
                }

                details.AppendLine($"<i>{HtmlEncode(key)}:</i> {HtmlEncode(formatted)}");
            }

            sb.AppendLine($"<blockquote expandable>{details.ToString().TrimEnd()}</blockquote>");
        }

        return sb.ToString().TrimEnd();
    }

    private static string HtmlEncode(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private static (long ChatId, int? ThreadId) ParseConversationId(string conversationId)
    {
        var parts = conversationId.Split(':');
        var chatId = long.Parse(parts[0]);
        var threadIdVal = long.Parse(parts[1]);

        return threadIdVal == chatId
            ? (chatId, null)
            : (chatId, Convert.ToInt32(threadIdVal));
    }
}
```

`McpChannelServiceBus/McpTools/RequestApprovalTool.cs` — change the parameter type and attribute (body otherwise unchanged):

```csharp
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval — ServiceBus auto-approves all tools")]
    public static string McpRun(
        [Description("Conversation ID (correlationId)")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests)
    {
        _ = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        return mode == ApprovalMode.Notify ? "notified" : "approved";
    }
```

`McpServerScheduling/McpTools/RequestApprovalTool.cs` — change the parameter type and attribute, and add `using Domain.DTOs.Channel;`:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval — scheduling auto-approves all tools")]
    public static string McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests)
        => mode == ApprovalMode.Notify ? "notified" : "approved";
}
```

- [ ] **Step 6: Update `ApprovalService`**

In `McpChannelSignalR/Services/ApprovalService.cs`, use `p.Requests` directly and delete `DeserializeRequests`. In `RequestApprovalAsync` replace `var requests = DeserializeRequests(p.Requests);` with:

```csharp
        var requests = p.Requests;
```

In `NotifyAutoApprovedAsync` replace `var requests = DeserializeRequests(p.Requests);` with:

```csharp
        var requests = p.Requests;
```

Delete the `DeserializeRequests` method:

```csharp
    private static IReadOnlyList<ToolApprovalRequest> DeserializeRequests(string requestsJson)
    {
        return JsonSerializer.Deserialize<List<ToolApprovalRequest>>(requestsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
```

Keep `using System.Text.Json;` — it is still used by `FormatArgumentValue` (the `JsonElement` cases).

- [ ] **Step 7: Run test + build**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelProtocolDtoTests"`
Expected: PASS.

Run: `dotnet build agent.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add Domain/DTOs/Channel/RequestApprovalParams.cs Infrastructure/Clients/Channels/McpChannelConnection.cs McpChannelSignalR/McpTools/RequestApprovalTool.cs McpChannelTelegram/McpTools/RequestApprovalTool.cs McpChannelServiceBus/McpTools/RequestApprovalTool.cs McpServerScheduling/McpTools/RequestApprovalTool.cs McpChannelSignalR/Services/ApprovalService.cs Tests/Unit/Domain/Channel/ChannelProtocolDtoTests.cs
git commit -m "feat(channel): make request_approval requests a native typed list"
```

---

## Task 7: `register_agents` native-typed list (atomic)

**Files:**
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs` (`RegisterAgentsAsync`)
- Modify: `McpChannelSignalR/McpTools/RegisterAgentsTool.cs`
- Test: `Tests/Unit/McpChannelSignalR/RegisterAgentsToolTests.cs`

- [ ] **Step 1: Rewrite the failing tests**

Replace the contents of `Tests/Unit/McpChannelSignalR/RegisterAgentsToolTests.cs` (the typed signature makes the old string-format test obsolete; drop it):

```csharp
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpChannelSignalR.McpTools;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class RegisterAgentsToolTests
{
    [Fact]
    public void McpRun_WithTypedAgents_ReplacesCatalogAndBroadcastsUpdate()
    {
        var catalog = new MutableAgentCatalog();
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        var result = tool.McpRun([new AgentCatalogEntry("jonas", "Jonas", "general")]);

        result.ShouldBe("registered 1 agents");
        catalog.Exists("jonas").ShouldBeTrue();
        sender.Verify(
            s => s.SendAsync("OnAgentsUpdated", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void McpRun_WithEmptyList_ClearsCatalog()
    {
        var catalog = new MutableAgentCatalog();
        catalog.Replace([new AgentCatalogEntry("old", "Old", null)]);
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        var result = tool.McpRun([]);

        result.ShouldBe("registered 0 agents");
        catalog.GetAll().ShouldBeEmpty();
        sender.Verify(
            s => s.SendAsync("OnAgentsUpdated", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RegisterAgentsToolTests"`
Expected: FAIL — build error: `McpRun` takes `string`, not `IReadOnlyList<AgentCatalogEntry>`.

- [ ] **Step 3: Retype the tool**

Replace `McpChannelSignalR/McpTools/RegisterAgentsTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog, IHubNotificationSender hubSender)
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agents available to WebChat (replaces any previously registered set)")]
    public string McpRun([Description("Agents available to WebChat")] IReadOnlyList<AgentCatalogEntry> agents)
    {
        catalog.Replace(agents);
        // best-effort UI refresh; a client-push failure must not block registration
        _ = hubSender.SendAsync("OnAgentsUpdated", agents);
        return $"registered {agents.Count} agents";
    }
}
```

- [ ] **Step 4: Update the agent caller**

In `Infrastructure/Clients/Channels/McpChannelConnection.cs`, change `RegisterAgentsAsync`'s probe and call:

```csharp
        if (tools.All(t => t.Name != ChannelProtocol.RegisterAgentsTool))
        {
            return;
        }

        await _client.CallToolAsync(
            ChannelProtocol.RegisterAgentsTool,
            ChannelProtocol.ToArguments(new RegisterAgentsParams { Agents = agents }),
            cancellationToken: ct);
```

- [ ] **Step 5: Run test + build**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RegisterAgentsToolTests"`
Expected: PASS (2 tests).

Run: `dotnet build agent.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Clients/Channels/McpChannelConnection.cs McpChannelSignalR/McpTools/RegisterAgentsTool.cs Tests/Unit/McpChannelSignalR/RegisterAgentsToolTests.cs
git commit -m "feat(channel): make register_agents a native typed list"
```

---

## Task 8: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Clean build of the whole solution**

Run: `dotnet build agent.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Confirm no stale references remain**

Run: `grep -rn "SchedulePayload\|DeserializeRequests" --include=*.cs . | grep -v /obj/`
Expected: no matches.

Run: `grep -rn "JsonSerializer.Serialize(requests)\|JsonSerializer.Serialize(agents)" --include=*.cs .`
Expected: no matches.

- [ ] **Step 3: Run the unit suite**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E"`
Expected: PASS except the known Docker-baseline failures (`DockerUnavailableException` from Redis-testcontainer integration tests). There must be **no new failures** in `Domain.Channel`, `Infrastructure.Channels`, `McpServerScheduling`, or `McpChannelSignalR` unit namespaces.

- [ ] **Step 4: Run the scheduling integration round-trip (if Docker is available)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpSchedulingServerTests"`
Expected: PASS if Docker is available (real Redis); otherwise `DockerUnavailableException` (baseline, acceptable). A pass confirms the end-to-end notification + tool path works against a real MCP server.

- [ ] **Step 5: Final commit (if any verification fixups were needed)**

```bash
git add -A
git commit -m "test(channel): verify type-safe channel protocol end to end"
```

(Skip if the working tree is clean.)

---

## Self-Review (completed by plan author)

**Spec coverage:** `ChannelProtocol` (T1) ✓; notification records + `RegisterAgentsParams` (T2) ✓; typed notification consumer + log-and-skip + logger DI (T3) ✓; record-based publishers incl. Scheduling `SchedulePayload` removal + `FirePlan` retype (T4) ✓; `send_reply`/`create_conversation` via `ToArguments` + name constants (T5) ✓; `request_approval` native-typed list incl. Telegram local-`ToolRequest` deletion and `ApprovalService` simplification (T6) ✓; `register_agents` native-typed list (T7) ✓; string-enum wire format preserved via `JsonStringEnumConverter` (T1, asserted T1/T6) ✓; build-green verification + stale-reference scan (T8) ✓.

**Placeholder scan:** none — every code step contains complete code.

**Type consistency:** `ChannelProtocol.{ToArguments,Deserialize,SerializerOptions}` and the name constants are used identically across T3–T7; `ChannelMessageNotification`/`ChannelCancelNotification`/`RegisterAgentsParams` property names match between producer, consumer, and tests; `RequestApprovalParams.Requests : IReadOnlyList<ToolApprovalRequest>` is consistent across the DTO, agent callers, all four tools, and `ApprovalService`; `RegisterAgentsTool.McpRun(IReadOnlyList<AgentCatalogEntry>)` matches the agent caller's `RegisterAgentsParams.Agents`.
