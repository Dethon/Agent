# Push Notification Topic Name — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show the topic name as the push notification title when an agent finishes responding, instead of the generic "New response".

**Architecture:** Add `TopicName` to the in-memory `ChannelSession` record. Thread it through `SessionService.StartSession` → `ChatHub.StartSession` → client `StartSessionAsync`. Read it back in `StreamService.CompleteStream` and pass to `SendPushNotificationAsync` as the notification title. Fall back to "New response" when null.

**Tech Stack:** C# / .NET 10, SignalR, Shouldly, Moq

---

## File Map

| Action | File | Responsibility |
|--------|------|---------------|
| Modify | `McpChannelSignalR/Internal/ChannelSession.cs` | Add `TopicName` property |
| Modify | `McpChannelSignalR/Services/SessionService.cs` | Accept and store `topicName` |
| Modify | `McpChannelSignalR/Hubs/ChatHub.cs` | Accept `topicName` from client |
| Modify | `McpChannelSignalR/Services/StreamService.cs` | Use topic name in push notification |
| Modify | `WebChat.Client/Services/ChatSessionService.cs` | Pass `topic.Name` to hub |
| Modify | `Tests/Unit/McpChannelSignalR/SessionServiceTests.cs` | Test topic name storage |
| Modify | `Tests/Unit/McpChannelSignalR/StreamServiceTests.cs` | Test push notification uses topic name |

---

### Task 1: Add TopicName to ChannelSession and SessionService

**Files:**
- Modify: `McpChannelSignalR/Internal/ChannelSession.cs:3`
- Modify: `McpChannelSignalR/Services/SessionService.cs:20,28`
- Test: `Tests/Unit/McpChannelSignalR/SessionServiceTests.cs`

- [ ] **Step 1: Write failing test — StartSession stores TopicName**

Add this test to `SessionServiceTests.cs`:

```csharp
[Fact]
public void StartSession_WithTopicName_StoresTopicName()
{
    _sut.StartSession("topic1", "agent1", 100, 200, topicName: "My topic");

    _sut.TryGetSession("topic1", out var session).ShouldBeTrue();
    session!.TopicName.ShouldBe("My topic");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "StartSession_WithTopicName_StoresTopicName" --no-restore`
Expected: Compilation error — `ChannelSession` has no `TopicName` property, `StartSession` has no `topicName` parameter.

- [ ] **Step 3: Add TopicName to ChannelSession**

In `McpChannelSignalR/Internal/ChannelSession.cs`, change:

```csharp
public record ChannelSession(string AgentId, long ChatId, long ThreadId, string? SpaceSlug = null);
```

to:

```csharp
public record ChannelSession(string AgentId, long ChatId, long ThreadId, string? SpaceSlug = null, string? TopicName = null);
```

- [ ] **Step 4: Add topicName parameter to SessionService.StartSession**

In `McpChannelSignalR/Services/SessionService.cs`, change the `StartSession` method signature and body:

```csharp
public bool StartSession(string topicId, string agentId, long chatId, long threadId, string? spaceSlug = null, string? topicName = null)
{
    var session = new ChannelSession(agentId, chatId, threadId, spaceSlug, topicName);
    _sessions[topicId] = session;
    _chatToTopic[chatId] = topicId;
    _conversationToTopic[$"{chatId}:{threadId}"] = topicId;
    return true;
}
```

- [ ] **Step 5: Thread topicName through CreateConversationAsync**

In `SessionService.CreateConversationAsync`, change line 20:

```csharp
StartSession(topicId, agentId, chatId, threadId, spaceSlug: "default");
```

to:

```csharp
StartSession(topicId, agentId, chatId, threadId, spaceSlug: "default", topicName: p.TopicName);
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "SessionServiceTests" --no-restore`
Expected: All tests PASS (existing tests unchanged — `topicName` defaults to `null`).

- [ ] **Step 7: Commit**

```bash
git add McpChannelSignalR/Internal/ChannelSession.cs McpChannelSignalR/Services/SessionService.cs Tests/Unit/McpChannelSignalR/SessionServiceTests.cs
git commit -m "feat: add TopicName to ChannelSession and SessionService"
```

---

### Task 2: Use TopicName in push notification

**Files:**
- Modify: `McpChannelSignalR/Services/StreamService.cs:138-179`
- Test: `Tests/Unit/McpChannelSignalR/StreamServiceTests.cs`

- [ ] **Step 1: Write failing test — push notification uses topic name as title**

Add this test to `StreamServiceTests.cs`:

```csharp
[Fact]
public async Task WriteReplyAsync_StreamComplete_SendsPushNotificationWithTopicName()
{
    _sessionService.StartSession("topic1", "agent1", 100, 200, spaceSlug: "myspace", topicName: "Apartment search");
    _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

    await _sut.WriteReplyAsync(new SendReplyParams
    { ConversationId = "100:200", Content = "", ContentType = "stream_complete", IsComplete = true });

    // Allow fire-and-forget to complete
    await Task.Delay(100);

    _pushNotification.Verify(p => p.SendToSpaceAsync(
        "myspace",
        "Apartment search",
        "The agent has finished responding",
        "/myspace",
        It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "WriteReplyAsync_StreamComplete_SendsPushNotificationWithTopicName" --no-restore`
Expected: FAIL — the title is "New response", not "Apartment search".

- [ ] **Step 3: Write failing test — push notification falls back when no topic name**

Add this test to `StreamServiceTests.cs`:

```csharp
[Fact]
public async Task WriteReplyAsync_StreamComplete_FallsBackToDefaultTitleWhenNoTopicName()
{
    _sessionService.StartSession("topic1", "agent1", 100, 200, spaceSlug: "myspace");
    _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

    await _sut.WriteReplyAsync(new SendReplyParams
    { ConversationId = "100:200", Content = "", ContentType = "stream_complete", IsComplete = true });

    // Allow fire-and-forget to complete
    await Task.Delay(100);

    _pushNotification.Verify(p => p.SendToSpaceAsync(
        "myspace",
        "New response",
        "The agent has finished responding",
        "/myspace",
        It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "WriteReplyAsync_StreamComplete_FallsBackToDefaultTitleWhenNoTopicName" --no-restore`
Expected: FAIL — `SendToSpaceAsync` is never called (no push notification sent without spaceSlug in current tests that don't set it).

Wait — the existing `WriteReplyAsync_StreamComplete_CompletesStream` test at line 63 calls `StartSession("topic1", "agent1", 100, 200)` without a `spaceSlug`, so `SpaceSlug` is null and no push is sent. The fallback test above sets `spaceSlug: "myspace"` so push is triggered but topic name is null — this will correctly test the fallback. The test should fail because the current code sends "New response" (which is actually what we want for the fallback). Let me reconsider — the fallback test should actually PASS immediately since the current code already sends "New response". That's fine; it's a GREEN test for regression protection.

- [ ] **Step 5: Update CompleteStream and SendPushNotificationAsync**

In `McpChannelSignalR/Services/StreamService.cs`, change `CompleteStream`:

```csharp
private void CompleteStream(string topicId)
{
    string? spaceSlug = null;
    string? topicName = null;

    lock (_streamLock)
    {
        // Resolve space slug and topic name before cleanup
        if (sessionService.TryGetSession(topicId, out var session))
        {
            spaceSlug = session?.SpaceSlug;
            topicName = session?.TopicName;
        }

        if (_responseChannels.TryRemove(topicId, out var channel))
        {
            channel.Complete();
        }

        CleanupStreamState(topicId);
    }

    if (spaceSlug is not null)
    {
        _ = SendPushNotificationAsync(spaceSlug, topicName);
    }
}
```

Change `SendPushNotificationAsync`:

```csharp
private async Task SendPushNotificationAsync(string spaceSlug, string? topicName)
{
    try
    {
        var url = $"/{spaceSlug}";
        await pushNotificationService.SendToSpaceAsync(
            spaceSlug,
            topicName ?? "New response",
            "The agent has finished responding",
            url);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex, "Failed to send push notification for space {SpaceSlug}", spaceSlug);
    }
}
```

- [ ] **Step 6: Run all StreamService tests**

Run: `dotnet test Tests/ --filter "StreamServiceTests" --no-restore`
Expected: All tests PASS.

- [ ] **Step 7: Commit**

```bash
git add McpChannelSignalR/Services/StreamService.cs Tests/Unit/McpChannelSignalR/StreamServiceTests.cs
git commit -m "feat: use topic name as push notification title"
```

---

### Task 3: Thread TopicName through ChatHub and WebChat client

**Files:**
- Modify: `McpChannelSignalR/Hubs/ChatHub.cs:57-60`
- Modify: `WebChat.Client/Services/ChatSessionService.cs:21-22`

- [ ] **Step 1: Update ChatHub.StartSession to accept topicName**

In `McpChannelSignalR/Hubs/ChatHub.cs`, change:

```csharp
public bool StartSession(string agentId, string topicId, long chatId, long threadId)
{
    return sessionService.StartSession(topicId, agentId, chatId, threadId, CurrentSpaceSlug);
}
```

to:

```csharp
public bool StartSession(string agentId, string topicId, long chatId, long threadId, string? topicName = null)
{
    return sessionService.StartSession(topicId, agentId, chatId, threadId, CurrentSpaceSlug, topicName);
}
```

- [ ] **Step 2: Update WebChat client to pass topic name**

In `WebChat.Client/Services/ChatSessionService.cs`, change:

```csharp
var success = await hubConnection.InvokeAsync<bool>(
    "StartSession", topic.AgentId, topic.TopicId, topic.ChatId, topic.ThreadId);
```

to:

```csharp
var success = await hubConnection.InvokeAsync<bool>(
    "StartSession", topic.AgentId, topic.TopicId, topic.ChatId, topic.ThreadId, topic.Name);
```

- [ ] **Step 3: Run full test suite**

Run: `dotnet test Tests/ --no-restore`
Expected: All tests PASS. No test changes needed — the `topicName` parameter is optional with a null default, so all existing callers (tests and `CreateConversationTool`) continue to work.

- [ ] **Step 4: Commit**

```bash
git add McpChannelSignalR/Hubs/ChatHub.cs WebChat.Client/Services/ChatSessionService.cs
git commit -m "feat: pass topic name from WebChat client to hub session"
```
