# Voice Room Awareness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface the satellite's physical room to the agent's LLM with every voice message, so the model can scope actions to the room, reference it naturally, and route announcements back to it.

**Architecture:** Carry an optional `Location` string end-to-end (`ChannelMessageNotification` → `ChannelMessage` → `ChatMessage` annotation), reusing the existing sender/timestamp annotation mechanism. The voice `TranscriptDispatcher` populates it from `SatelliteConfig.Room`; `OpenRouterChatClient` folds it into the user-message prefix the LLM sees. Null for all non-voice channels — fully backward-compatible.

**Tech Stack:** .NET 10, xUnit, Moq, Shouldly, Microsoft.Extensions.AI `ChatMessage`.

**Conventions (must follow):**
- NO trailing newline in any `.cs` file (including tests).
- Test method names: `{Method}_{Scenario}_{ExpectedResult}`.
- File-scoped namespaces, `record` DTOs, LINQ over loops.
- Commit after each task.

---

### Task 1: `GetLocation` / `SetLocation` message annotation

**Files:**
- Modify: `Domain/Extensions/ChatMessageExtensions.cs`
- Test: `Tests/Unit/Domain/ChatMessageSerializationTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these two tests inside the `ChatMessageSerializationTests` class (after the existing sender tests):

```csharp
[Fact]
public void SetAndGetLocation_StoresAndRetrievesValue()
{
    var msg = new ChatMessage(ChatRole.User, "Hello");

    msg.SetLocation("the office");

    msg.AdditionalProperties.ShouldNotBeNull();
    msg.AdditionalProperties["Location"].ShouldBe("the office");
    msg.GetLocation().ShouldBe("the office");
}

[Fact]
public void GetLocation_ReturnsValueAfterJsonRoundtrip()
{
    var msg = new ChatMessage(ChatRole.User, "Hello");
    msg.SetLocation("the office");

    var json = JsonSerializer.Serialize(msg);
    var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

    deserialized.ShouldNotBeNull();
    deserialized.GetLocation().ShouldBe("the office");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~ChatMessageSerializationTests" -v q`
Expected: FAIL — compile error, `GetLocation`/`SetLocation` not defined.

- [ ] **Step 3: Implement the extension members**

In `Domain/Extensions/ChatMessageExtensions.cs`, add the key constant next to the others (after line 12 `private const string MemoryContextKey = "MemoryContext";`):

```csharp
    private const string LocationKey = "Location";
```

Then, inside `extension(ChatMessage message)`, add these members (e.g. after `SetSenderId`):

```csharp
        public string? GetLocation()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(LocationKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetLocation(string? location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[LocationKey] = location;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~ChatMessageSerializationTests" -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Extensions/ChatMessageExtensions.cs Tests/Unit/Domain/ChatMessageSerializationTests.cs
git commit -m "feat(voice): add Get/SetLocation message annotation"
```

---

### Task 2: Add `Location` to the channel DTOs

**Files:**
- Modify: `Domain/DTOs/Channel/ChannelMessageNotification.cs`
- Modify: `Domain/DTOs/ChannelMessage.cs`

These are pure optional data carriers (no behavior); they are exercised by the tests in Tasks 3–5. Verify by build only.

- [ ] **Step 1: Add the field to `ChannelMessageNotification`**

In `Domain/DTOs/Channel/ChannelMessageNotification.cs`, add after the `Origin` property (line 13):

```csharp
    public string? Location { get; init; }
```

- [ ] **Step 2: Add the field to `ChannelMessage`**

In `Domain/DTOs/ChannelMessage.cs`, add after the `Origin` property (line 15):

```csharp
    public string? Location { get; init; }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build Domain`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Domain/DTOs/Channel/ChannelMessageNotification.cs Domain/DTOs/ChannelMessage.cs
git commit -m "feat(voice): carry optional Location on channel message DTOs"
```

---

### Task 3: Map `Location` through `McpChannelConnection`

**Files:**
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs:85-94`
- Test: `Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs`

- [ ] **Step 1: Write the failing tests**

Add these two tests to the `McpChannelConnectionParsingTests` class:

```csharp
[Fact]
public async Task HandleChannelMessageNotification_WithLocation_ParsesIt()
{
    var conn = new McpChannelConnection("voice");
    conn.HandleChannelMessageNotification(Json("""
    {"conversationId":"c1","content":"lights on","sender":"household","location":"the office"}
    """));

    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    await foreach (var msg in conn.Messages.WithCancellation(cts.Token))
    {
        msg.Location.ShouldBe("the office");
        break;
    }
}

[Fact]
public async Task HandleChannelMessageNotification_WithoutLocation_LeavesItNull()
{
    var conn = new McpChannelConnection("signalr");
    conn.HandleChannelMessageNotification(Json("""
    {"conversationId":"c1","content":"hi","sender":"user"}
    """));

    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    await foreach (var msg in conn.Messages.WithCancellation(cts.Token))
    {
        msg.Location.ShouldBeNull();
        break;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~McpChannelConnectionParsingTests" -v q`
Expected: FAIL — `HandleChannelMessageNotification_WithLocation_ParsesIt` fails because `msg.Location` is null (mapping not wired).

- [ ] **Step 3: Wire the mapping**

In `Infrastructure/Clients/Channels/McpChannelConnection.cs`, in the `HandleChannelMessageNotification` handler where the `ChannelMessage` is built (the block at lines 85-94), add the `Location` mapping after `Origin = notification.Origin`:

```csharp
        var message = new ChannelMessage
        {
            ConversationId = notification.ConversationId,
            Content = notification.Content,
            Sender = notification.Sender,
            ChannelId = ChannelId,
            AgentId = notification.AgentId,
            ReplyTo = notification.ReplyTo,
            Origin = notification.Origin,
            Location = notification.Location
        };
```

(Leave the `HandleChannelCancelNotification` block unchanged — cancel messages have no location.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~McpChannelConnectionParsingTests" -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Channels/McpChannelConnection.cs Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs
git commit -m "feat(voice): map Location through McpChannelConnection"
```

---

### Task 4: Attach `Location` onto the user message in `ChatMonitor`

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs:158-160`

This is a one-line wiring change mirroring `SetSenderId`/`SetTimestamp`; the `ChatMonitor` message pipeline is not unit-testable in isolation here, so verify by build. The end-to-end effect is covered by Task 6 (dispatcher emits Location) plus Task 5 (prefix rendering consumes it).

- [ ] **Step 1: Add the `SetLocation` call**

In `Domain/Monitor/ChatMonitor.cs`, in the `default:` branch where the user message is built (lines 158-160), add the `SetLocation` call after `SetSenderId`:

```csharp
                        var userMessage = new ChatMessage(ChatRole.User, x.Message.Content);
                        userMessage.SetSenderId(x.Message.Sender);
                        userMessage.SetLocation(x.Message.Location);
                        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build Domain`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs
git commit -m "feat(voice): attach Location annotation to inbound user message"
```

---

### Task 5: Render `Location` into the LLM message prefix

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs:74-88`
- Test: `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientPrefixTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientPrefixTests.cs`:

```csharp
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class OpenRouterChatClientPrefixTests : IDisposable
{
    private readonly Mock<IChatClient> _innerClient = new();
    private readonly OpenRouterChatClient _sut;
    private IReadOnlyList<ChatMessage> _captured = [];

    public OpenRouterChatClientPrefixTests()
    {
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (messages, _, _) => _captured = messages.ToList())
            .Returns(Array.Empty<ChatResponseUpdate>().ToAsyncEnumerable());

        _sut = new OpenRouterChatClient(_innerClient.Object, "test-model");
    }

    public void Dispose() => _sut.Dispose();

    private string FirstText() =>
        _captured[0].Contents.OfType<TextContent>().First().Text;

    [Fact]
    public async Task GetStreamingResponseAsync_WithSenderAndLocation_PrefixesRoom()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.SetLocation("the office");

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("Message from household (in the office):");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithSenderNoLocation_OmitsRoom()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("Message from household:");
        FirstText().ShouldNotContain("(in");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~OpenRouterChatClientPrefixTests" -v q`
Expected: FAIL — `WithSenderAndLocation_PrefixesRoom` fails: prefix is `Message from household:` (location not yet rendered).

- [ ] **Step 3: Implement the prefix change**

In `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`, replace the prefix block (lines 74-88) with:

```csharp
            var msgSender = newMessage.GetSenderId();
            var timestamp = newMessage.GetTimestamp();
            var location = newMessage.GetLocation();
            if (newMessage.Role == ChatRole.User && (msgSender is not null || timestamp is not null))
            {
                var senderSegment = msgSender is null
                    ? null
                    : string.IsNullOrWhiteSpace(location)
                        ? $"Message from {msgSender}"
                        : $"Message from {msgSender} (in {location})";

                var prefix = (senderSegment, timestamp) switch
                {
                    (not null, not null) => $"[Current time: {timestamp:yyyy-MM-dd HH:mm:ss zzz}] {senderSegment}:\n",
                    (not null, null) => $"{senderSegment}:\n",
                    (null, not null) => $"[Current time: {timestamp:yyyy-MM-dd HH:mm:ss zzz}]:\n",
                    _ => ""
                };
                newMessage.Contents = newMessage.Contents
                    .Prepend(new TextContent(prefix))
                    .ToList();
            }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~OpenRouterChatClientPrefixTests" -v q`
Expected: PASS.

- [ ] **Step 5: Run the existing metrics tests to confirm no regression**

Run: `dotnet test Tests --filter "FullyQualifiedName~OpenRouterChatClientMetricsTests" -v q`
Expected: PASS (prefix change preserves sender/timestamp behavior).

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientPrefixTests.cs
git commit -m "feat(voice): render room location into LLM message prefix"
```

---

### Task 6: Populate `Location` from the satellite room in `TranscriptDispatcher` + config wording

**Files:**
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs:59-68`
- Modify: `McpChannelVoice/appsettings.json`, `McpChannelVoice/appsettings.Development.json`
- Test: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`

- [ ] **Step 1: Write the failing test**

Add this test to the `TranscriptDispatcherTests` class (the existing `Session()` helper uses `Room = "Kitchen"`):

```csharp
[Fact]
public async Task DispatchAsync_GoodTranscript_EmitsRoomAsLocation()
{
    var (sut, _, emitter) = Build();

    await sut.DispatchAsync(
        Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", default);

    emitter.Captured.Count.ShouldBe(1);
    emitter.Captured[0].Location.ShouldBe("Kitchen");
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~TranscriptDispatcherTests" -v q`
Expected: FAIL — `EmitsRoomAsLocation` fails: `Location` is null (dispatcher does not set it).

- [ ] **Step 3: Set `Location` on the emitted notification**

In `McpChannelVoice/Services/TranscriptDispatcher.cs`, in the `EmitMessageNotificationAsync` call (lines 59-68), add `Location` after `Sender`:

```csharp
        await emitter.EmitMessageNotificationAsync(
            new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = session.Config.Identity,
                Location = session.Config.Room,
                Content = transcript.Text,
                AgentId = agentId,
                Timestamp = DateTimeOffset.UtcNow
            },
            ct);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~TranscriptDispatcherTests" -v q`
Expected: PASS (all dispatcher tests).

- [ ] **Step 5: Set the sample satellite room to a natural phrase**

The rendered prefix is `Message from {sender} (in {room})`, so the room must read naturally after "in ". Find the current value:

Run: `grep -rn '"Room"' McpChannelVoice/appsettings.json McpChannelVoice/appsettings.Development.json`

In each file, change the satellite's `"Room"` value from its id-style value (e.g. `"FranOffice"`) to `"the office"`. Leave all other keys (`Identity`, `Address`, `WakeWord`) unchanged.

- [ ] **Step 6: Build to confirm config still binds**

Run: `dotnet build McpChannelVoice`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Services/TranscriptDispatcher.cs McpChannelVoice/appsettings.json McpChannelVoice/appsettings.Development.json Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs
git commit -m "feat(voice): emit satellite room as message Location"
```

---

### Task 7: Full verification

- [ ] **Step 1: Run the full affected test set**

Run:
```bash
dotnet test Tests --filter "FullyQualifiedName~ChatMessageSerializationTests|FullyQualifiedName~McpChannelConnectionParsingTests|FullyQualifiedName~OpenRouterChatClientPrefixTests|FullyQualifiedName~OpenRouterChatClientMetricsTests|FullyQualifiedName~TranscriptDispatcherTests" -v q
```
Expected: all PASS.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeded, no new warnings in the touched files.

---

## Notes for the implementer

- **Backward compatibility:** `Location` is optional everywhere; non-voice channels (Telegram, WebChat, ServiceBus, scheduling) never set it, so their prefixes are byte-for-byte unchanged. The `(null, not null)` timestamp-only branch is preserved.
- **Why no ChatMonitor unit test (Task 4):** it is a single wiring line identical in shape to the adjacent `SetSenderId`; its behavior is observable only through the full agent pipeline, which Tasks 5–6 already cover end to end. Don't add a brittle pipeline test for it.
- **Downstream goals are free:** once the room name is in the model's context, Home-Assistant area scoping and `AnnounceTarget.Room` routing are the model's job via existing tools — no further code in this plan.
