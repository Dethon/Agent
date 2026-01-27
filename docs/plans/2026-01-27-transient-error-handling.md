# Transient Error Handling Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Filter transient errors (OperationCanceledException, empty messages) from appearing as chat messages when PWA reconnects after background.

**Architecture:** Add `IsTransientError` helper to `StreamingService`, use exception filters (`when` clause) to only show real errors. Transient errors silently complete, letting the existing reconnection flow handle recovery.

**Tech Stack:** C# 10, xUnit, Shouldly

---

### Task 1: Add Exception Throwing Capability to FakeChatMessagingService

**Files:**
- Modify: `Tests/Unit/WebChat/Fixtures/FakeChatMessagingService.cs`

**Step 1: Add exception field and setter**

Add after line 13 (`private readonly TaskCompletionSource _completionSource = new();`):

```csharp
private Exception? _exceptionToThrow;

public void SetExceptionToThrow(Exception? exception) => _exceptionToThrow = exception;
```

**Step 2: Add exception throwing to SendMessageAsync**

Modify `SendMessageAsync` to check for exception at start. Replace lines 74-91:

```csharp
public async IAsyncEnumerable<ChatStreamMessage> SendMessageAsync(string topicId, string message,
    string? correlationId = null)
{
    if (_exceptionToThrow is not null)
    {
        throw _exceptionToThrow;
    }

    if (_blockUntilComplete)
    {
        await _completionSource.Task;
    }

    while (_enqueuedMessages.TryDequeue(out var msg))
    {
        if (StreamDelayMs > 0)
        {
            await Task.Delay(StreamDelayMs);
        }

        yield return msg;
    }
}
```

**Step 3: Add exception throwing to ResumeStreamAsync**

Modify `ResumeStreamAsync` to check for exception at start. Replace lines 93-104:

```csharp
public async IAsyncEnumerable<ChatStreamMessage> ResumeStreamAsync(string topicId)
{
    if (_exceptionToThrow is not null)
    {
        throw _exceptionToThrow;
    }

    while (_enqueuedMessages.TryDequeue(out var msg))
    {
        if (StreamDelayMs > 0)
        {
            await Task.Delay(StreamDelayMs);
        }

        yield return msg;
    }
}
```

**Step 4: Run existing tests to verify no regression**

Run: `dotnet test Tests/Unit --filter StreamingServiceTests`
Expected: All existing tests PASS

**Step 5: Commit**

```bash
git add Tests/Unit/WebChat/Fixtures/FakeChatMessagingService.cs
git commit -m "test: add exception throwing to FakeChatMessagingService"
```

---

### Task 2: Write Failing Tests for Transient Error Filtering

**Files:**
- Modify: `Tests/Unit/WebChat/Client/StreamingServiceTests.cs`

**Step 1: Write test for OperationCanceledException being silent**

Add in the `#region StreamResponseAsync Tests` section:

```csharp
[Fact]
public async Task StreamResponseAsync_WithOperationCanceledException_DoesNotAddErrorMessage()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    _messagingService.SetExceptionToThrow(new OperationCanceledException());

    await _service.StreamResponseAsync(topic, "test");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.ShouldNotContain(m => m.IsError);
}
```

**Step 2: Write test for TaskCanceledException being silent**

```csharp
[Fact]
public async Task StreamResponseAsync_WithTaskCanceledException_DoesNotAddErrorMessage()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    _messagingService.SetExceptionToThrow(new TaskCanceledException());

    await _service.StreamResponseAsync(topic, "test");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.ShouldNotContain(m => m.IsError);
}
```

**Step 3: Write test for empty message exception being silent**

```csharp
[Fact]
public async Task StreamResponseAsync_WithEmptyMessageException_DoesNotAddErrorMessage()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    _messagingService.SetExceptionToThrow(new Exception(""));

    await _service.StreamResponseAsync(topic, "test");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.ShouldNotContain(m => m.IsError);
}
```

**Step 4: Write test for real exceptions still showing errors**

```csharp
[Fact]
public async Task StreamResponseAsync_WithRealException_AddsErrorMessage()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    _messagingService.SetExceptionToThrow(new InvalidOperationException("Something went wrong"));

    await _service.StreamResponseAsync(topic, "test");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.ShouldContain(m => m.IsError && m.Content.Contains("Something went wrong"));
}
```

**Step 5: Write same tests for ResumeStreamResponseAsync**

Add in the `#region ResumeStreamResponseAsync Tests` section:

```csharp
[Fact]
public async Task ResumeStreamResponseAsync_WithOperationCanceledException_DoesNotAddErrorMessage()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
    _messagingService.SetExceptionToThrow(new OperationCanceledException());

    await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.ShouldNotContain(m => m.IsError);
}

[Fact]
public async Task ResumeStreamResponseAsync_WithTaskCanceledException_DoesNotAddErrorMessage()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
    _messagingService.SetExceptionToThrow(new TaskCanceledException());

    await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.ShouldNotContain(m => m.IsError);
}

[Fact]
public async Task ResumeStreamResponseAsync_WithEmptyMessageException_DoesNotAddErrorMessage()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
    _messagingService.SetExceptionToThrow(new Exception(""));

    await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.ShouldNotContain(m => m.IsError);
}

[Fact]
public async Task ResumeStreamResponseAsync_WithRealException_AddsErrorMessage()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
    _messagingService.SetExceptionToThrow(new InvalidOperationException("Something went wrong"));

    await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.ShouldContain(m => m.IsError && m.Content.Contains("Something went wrong"));
}
```

**Step 6: Run tests to verify they fail**

Run: `dotnet test Tests/Unit --filter "StreamingServiceTests&(OperationCanceledException|TaskCanceledException|EmptyMessageException|RealException)"`
Expected: FAIL - transient error tests fail because errors are still being added

**Step 7: Commit failing tests**

```bash
git add Tests/Unit/WebChat/Client/StreamingServiceTests.cs
git commit -m "test: add failing tests for transient error filtering"
```

---

### Task 3: Implement Transient Error Filtering

**Files:**
- Modify: `WebChat.Client/Services/Streaming/StreamingService.cs`

**Step 1: Add IsTransientError helper method**

Add before `CreateErrorMessage` method (around line 376):

```csharp
private static bool IsTransientError(Exception ex) =>
    ex is OperationCanceledException or TaskCanceledException ||
    string.IsNullOrWhiteSpace(ex.Message);
```

**Step 2: Modify catch block in StreamResponseAsync**

Replace lines 203-206:

```csharp
catch (Exception ex)
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage($"Error: {ex.Message}")));
}
```

With:

```csharp
catch (Exception ex) when (!IsTransientError(ex))
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage($"Error: {ex.Message}")));
}
catch
{
    // Transient error (connection lost, operation cancelled) - reconnection flow handles recovery
}
```

**Step 3: Modify catch block in ResumeStreamResponseAsync**

Replace lines 365-369:

```csharp
catch (Exception ex)
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId,
        CreateErrorMessage($"Error resuming stream: {ex.Message}")));
}
```

With:

```csharp
catch (Exception ex) when (!IsTransientError(ex))
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId,
        CreateErrorMessage($"Error resuming stream: {ex.Message}")));
}
catch
{
    // Transient error (connection lost, operation cancelled) - reconnection flow handles recovery
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --filter StreamingServiceTests`
Expected: All tests PASS

**Step 5: Commit implementation**

```bash
git add WebChat.Client/Services/Streaming/StreamingService.cs
git commit -m "fix: filter transient errors from chat messages

OperationCanceledException, TaskCanceledException, and exceptions with
empty messages are now silently ignored instead of polluting chat with
error messages. The existing reconnection flow handles recovery.

Fixes PWA showing 'Error: ' and 'OperationCancelled' messages when
returning from background on Android."
```

---

### Task 4: Verify All Tests Pass

**Step 1: Run full test suite**

Run: `dotnet test`
Expected: All tests PASS

**Step 2: Manual verification (optional)**

If PWA is available for testing:
1. Open PWA on Android
2. Start a streaming message
3. Put app in background for 30+ seconds
4. Return to app
5. Verify: No error messages in chat, connection status shows reconnecting then connected
