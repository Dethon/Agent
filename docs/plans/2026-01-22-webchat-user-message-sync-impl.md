# WebChat User Message Sync Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Sync user messages to other browsers watching the same topic, maintaining chronological order.

**Architecture:** Write user messages to stream buffer and broadcast via SignalR notification. Buffer rebuild handles mixed user/assistant messages.

**Tech Stack:** C#, SignalR, Blazor WebAssembly

---

## Task 1: Add UserMessageInfo record and update ChatStreamMessage

**Files:**
- Create: `Domain/DTOs/WebChat/UserMessageInfo.cs`
- Modify: `Domain/DTOs/WebChat/ChatStreamMessage.cs`

**Step 1: Write the failing test**

Create test file `Tests/Unit/Domain/DTOs/WebChat/ChatStreamMessageTests.cs`:

```csharp
using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

public sealed class ChatStreamMessageTests
{
    [Fact]
    public void ChatStreamMessage_WithUserMessage_IndicatesUserRole()
    {
        var message = new ChatStreamMessage
        {
            Content = "Hello",
            UserMessage = new UserMessageInfo("alice")
        };

        message.UserMessage.ShouldNotBeNull();
        message.UserMessage.SenderId.ShouldBe("alice");
    }

    [Fact]
    public void ChatStreamMessage_WithoutUserMessage_IndicatesAssistantRole()
    {
        var message = new ChatStreamMessage
        {
            Content = "Hello"
        };

        message.UserMessage.ShouldBeNull();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatStreamMessageTests" --no-build`
Expected: FAIL with compilation error "UserMessageInfo not found"

**Step 3: Create UserMessageInfo record**

Create `Domain/DTOs/WebChat/UserMessageInfo.cs`:

```csharp
namespace Domain.DTOs.WebChat;

public record UserMessageInfo(string? SenderId);
```

**Step 4: Add UserMessage property to ChatStreamMessage**

Modify `Domain/DTOs/WebChat/ChatStreamMessage.cs` to add:

```csharp
public UserMessageInfo? UserMessage { get; init; }
```

Full file should be:

```csharp
namespace Domain.DTOs.WebChat;

public record ChatStreamMessage
{
    public string? Content { get; init; }
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public bool IsComplete { get; init; }
    public string? Error { get; init; }
    public string? MessageId { get; init; }
    public ToolApprovalRequestMessage? ApprovalRequest { get; init; }
    public long SequenceNumber { get; init; }
    public UserMessageInfo? UserMessage { get; init; }
}
```

**Step 5: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatStreamMessageTests"`
Expected: PASS

**Step 6: Commit**

```bash
git add Domain/DTOs/WebChat/UserMessageInfo.cs Domain/DTOs/WebChat/ChatStreamMessage.cs Tests/Unit/Domain/DTOs/WebChat/ChatStreamMessageTests.cs
git commit -m "feat(webchat): add UserMessageInfo to ChatStreamMessage for user message tracking"
```

---

## Task 2: Add UserMessageNotification and INotifier method

**Files:**
- Modify: `Domain/DTOs/WebChat/HubNotification.cs`
- Modify: `Domain/Contracts/INotifier.cs`
- Modify: `Infrastructure/Clients/Messaging/HubNotifier.cs`

**Step 1: Write the failing test**

Create test file `Tests/Unit/Infrastructure/HubNotifierTests.cs`:

```csharp
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class HubNotifierTests
{
    [Fact]
    public async Task NotifyUserMessageAsync_SendsCorrectNotification()
    {
        var sender = Substitute.For<IHubNotificationSender>();
        var notifier = new HubNotifier(sender);
        var notification = new UserMessageNotification("topic-1", "Hello", "alice");

        await notifier.NotifyUserMessageAsync(notification);

        await sender.Received(1).SendAsync(
            "OnUserMessage",
            Arg.Is<UserMessageNotification>(n =>
                n.TopicId == "topic-1" &&
                n.Content == "Hello" &&
                n.SenderId == "alice"),
            Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HubNotifierTests" --no-build`
Expected: FAIL with "UserMessageNotification not found" or "NotifyUserMessageAsync not found"

**Step 3: Add UserMessageNotification record**

Add to `Domain/DTOs/WebChat/HubNotification.cs`:

```csharp
public record UserMessageNotification(
    string TopicId,
    string Content,
    string? SenderId);
```

**Step 4: Add method to INotifier interface**

Add to `Domain/Contracts/INotifier.cs`:

```csharp
Task NotifyUserMessageAsync(UserMessageNotification notification,
    CancellationToken cancellationToken = default);
```

**Step 5: Implement in HubNotifier**

Add to `Infrastructure/Clients/Messaging/HubNotifier.cs`:

```csharp
public async Task NotifyUserMessageAsync(
    UserMessageNotification notification,
    CancellationToken cancellationToken = default)
{
    await sender.SendAsync("OnUserMessage", notification, cancellationToken);
}
```

**Step 6: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HubNotifierTests"`
Expected: PASS

**Step 7: Commit**

```bash
git add Domain/DTOs/WebChat/HubNotification.cs Domain/Contracts/INotifier.cs Infrastructure/Clients/Messaging/HubNotifier.cs Tests/Unit/Infrastructure/HubNotifierTests.cs
git commit -m "feat(webchat): add UserMessageNotification and INotifier.NotifyUserMessageAsync"
```

---

## Task 3: Write user message to buffer in WebChatMessengerClient

**Files:**
- Modify: `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`
- Modify: `Tests/Unit/Infrastructure/WebChatStreamManagerTests.cs`

**Step 1: Write the failing test**

Add to `Tests/Unit/Infrastructure/WebChatStreamManagerTests.cs`:

```csharp
[Fact]
public async Task WriteMessageAsync_WithUserMessage_BuffersUserMessage()
{
    const string topicId = "test-topic";
    _manager.CreateStream(topicId, "test prompt", null, CancellationToken.None);

    var userMessage = new ChatStreamMessage
    {
        Content = "Hello from user",
        UserMessage = new UserMessageInfo("alice")
    };

    await _manager.WriteMessageAsync(topicId, userMessage, CancellationToken.None);

    var state = _manager.GetStreamState(topicId);
    state.ShouldNotBeNull();
    state.BufferedMessages.ShouldNotBeEmpty();
    state.BufferedMessages[0].Content.ShouldBe("Hello from user");
    state.BufferedMessages[0].UserMessage.ShouldNotBeNull();
    state.BufferedMessages[0].UserMessage!.SenderId.ShouldBe("alice");
}
```

**Step 2: Run test to verify it passes (no changes needed to WriteMessageAsync)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WriteMessageAsync_WithUserMessage"`
Expected: PASS (WriteMessageAsync already handles any ChatStreamMessage)

**Step 3: Now add integration test for EnqueuePromptAndGetResponses writing user message**

This requires a more complex test setup. For now, we'll verify the behavior through the existing integration tests after modifying the code.

**Step 4: Modify EnqueuePromptAndGetResponses to write user message to buffer**

In `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`, modify `EnqueuePromptAndGetResponses` method.

Find this section (around line 157-162):
```csharp
var (broadcastChannel, linkedToken) = streamManager.CreateStream(topicId, message, sender, cancellationToken);
streamManager.TryIncrementPending(topicId);

await hubNotifier.NotifyStreamChangedAsync(
        new StreamChangedNotification(StreamChangeType.Started, topicId), cancellationToken)
    .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);
```

Replace with:
```csharp
var (broadcastChannel, linkedToken) = streamManager.CreateStream(topicId, message, sender, cancellationToken);
streamManager.TryIncrementPending(topicId);

// Write user message to buffer for other browsers to see on refresh
var userMessage = new ChatStreamMessage
{
    Content = message,
    UserMessage = new UserMessageInfo(sender)
};
await streamManager.WriteMessageAsync(topicId, userMessage, cancellationToken);

// Notify other browsers about the user message
await hubNotifier.NotifyUserMessageAsync(
        new UserMessageNotification(topicId, message, sender), cancellationToken)
    .SafeAwaitAsync(logger, "Failed to notify user message for topic {TopicId}", topicId);

await hubNotifier.NotifyStreamChangedAsync(
        new StreamChangedNotification(StreamChangeType.Started, topicId), cancellationToken)
    .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);
```

**Step 5: Modify EnqueuePrompt similarly**

Find `EnqueuePrompt` method (around line 184-210). After `streamManager.TryIncrementPending(topicId)` and before creating the prompt, add:

```csharp
// Write user message to buffer for other browsers to see on refresh
var userMessage = new ChatStreamMessage
{
    Content = message,
    UserMessage = new UserMessageInfo(sender)
};
// Fire and forget - don't block the enqueue
_ = streamManager.WriteMessageAsync(topicId, userMessage, CancellationToken.None);

// Notify other browsers about the user message
_ = hubNotifier.NotifyUserMessageAsync(
        new UserMessageNotification(topicId, message, sender), CancellationToken.None)
    .SafeAwaitAsync(logger, "Failed to notify user message for topic {TopicId}", topicId);
```

**Step 6: Run all WebChatStreamManager tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatStreamManager"`
Expected: PASS

**Step 7: Commit**

```bash
git add Infrastructure/Clients/Messaging/WebChatMessengerClient.cs Tests/Unit/Infrastructure/WebChatStreamManagerTests.cs
git commit -m "feat(webchat): write user messages to buffer and broadcast notification"
```

---

## Task 4: Add client-side SignalR handler for UserMessageNotification

**Files:**
- Modify: `WebChat.Client/State/Hub/IHubEventDispatcher.cs`
- Modify: `WebChat.Client/State/Hub/HubEventDispatcher.cs`
- Modify: `WebChat.Client/Services/SignalREventSubscriber.cs`

**Step 1: Add handler method to IHubEventDispatcher**

Add to `WebChat.Client/State/Hub/IHubEventDispatcher.cs`:

```csharp
void HandleUserMessage(UserMessageNotification notification);
```

Full file:
```csharp
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Hub;

public interface IHubEventDispatcher
{
    void HandleTopicChanged(TopicChangedNotification notification);
    void HandleStreamChanged(StreamChangedNotification notification);
    void HandleApprovalResolved(ApprovalResolvedNotification notification);
    void HandleToolCalls(ToolCallsNotification notification);
    void HandleUserMessage(UserMessageNotification notification);
}
```

**Step 2: Implement in HubEventDispatcher**

Add to `WebChat.Client/State/Hub/HubEventDispatcher.cs`:

```csharp
public void HandleUserMessage(UserMessageNotification notification)
{
    // Only add if we're watching this topic and message isn't from us
    var currentTopic = topicsStore.State.SelectedTopicId;
    if (currentTopic != notification.TopicId)
    {
        return;
    }

    dispatcher.Dispatch(new AddMessage(notification.TopicId, new ChatMessageModel
    {
        Role = "user",
        Content = notification.Content,
        SenderId = notification.SenderId
    }));
}
```

Add the necessary using statement at the top:
```csharp
using WebChat.Client.State.Messages;
```

**Step 3: Register handler in SignalREventSubscriber**

Add to `WebChat.Client/Services/SignalREventSubscriber.cs` in the `Subscribe()` method, after the existing subscriptions:

```csharp
_subscriptions.Add(
    hubConnection.On<UserMessageNotification>(
        "OnUserMessage", hubEventDispatcher.HandleUserMessage));
```

**Step 4: Build to verify compilation**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add WebChat.Client/State/Hub/IHubEventDispatcher.cs WebChat.Client/State/Hub/HubEventDispatcher.cs WebChat.Client/Services/SignalREventSubscriber.cs
git commit -m "feat(webchat): add client-side SignalR handler for user message notifications"
```

---

## Task 5: Update BufferRebuildUtility to handle mixed user/assistant messages

**Files:**
- Modify: `WebChat.Client/Services/Streaming/BufferRebuildUtility.cs`
- Modify: `Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs`

**Step 1: Write the failing tests**

Add to `Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs`:

```csharp
[Fact]
public void RebuildFromBuffer_WithUserMessage_IncludesInCompletedTurns()
{
    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "Hello from user", UserMessage = new UserMessageInfo("alice") },
        new() { Content = "Hi there!", MessageId = "msg-1" }
    };

    var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

    completedTurns.Count.ShouldBe(1);
    completedTurns[0].Role.ShouldBe("user");
    completedTurns[0].Content.ShouldBe("Hello from user");
    completedTurns[0].SenderId.ShouldBe("alice");
    streamingMessage.Content.ShouldBe("Hi there!");
}

[Fact]
public void RebuildFromBuffer_WithMixedMessages_PreservesChronologicalOrder()
{
    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "User msg 1", UserMessage = new UserMessageInfo("alice"), SequenceNumber = 1 },
        new() { Content = "Assistant response 1", MessageId = "msg-1", SequenceNumber = 2 },
        new() { IsComplete = true, MessageId = "msg-1", SequenceNumber = 3 },
        new() { Content = "User msg 2", UserMessage = new UserMessageInfo("bob"), SequenceNumber = 4 },
        new() { Content = "Assistant response 2", MessageId = "msg-2", SequenceNumber = 5 }
    };

    var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer, []);

    completedTurns.Count.ShouldBe(3);
    completedTurns[0].Role.ShouldBe("user");
    completedTurns[0].Content.ShouldBe("User msg 1");
    completedTurns[1].Role.ShouldBe("assistant");
    completedTurns[1].Content.ShouldBe("Assistant response 1");
    completedTurns[2].Role.ShouldBe("user");
    completedTurns[2].Content.ShouldBe("User msg 2");
    streamingMessage.Content.ShouldBe("Assistant response 2");
}

[Fact]
public void RebuildFromBuffer_UserMessageNotStripped_EvenIfInHistory()
{
    var buffer = new List<ChatStreamMessage>
    {
        new() { Content = "Hello", UserMessage = new UserMessageInfo("alice") }
    };
    var historyContent = new HashSet<string> { "Hello" };

    var (completedTurns, _) = BufferRebuildUtility.RebuildFromBuffer(buffer, historyContent);

    // User messages should NOT be stripped based on assistant history
    completedTurns.Count.ShouldBe(1);
    completedTurns[0].Content.ShouldBe("Hello");
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~BufferRebuildUtilityTests.RebuildFromBuffer_WithUserMessage"`
Expected: FAIL

**Step 3: Update BufferRebuildUtility to handle user messages**

Replace the entire `RebuildFromBuffer` method in `WebChat.Client/Services/Streaming/BufferRebuildUtility.cs`:

```csharp
public static (List<ChatMessageModel> CompletedTurns, ChatMessageModel StreamingMessage) RebuildFromBuffer(
    IReadOnlyList<ChatStreamMessage> bufferedMessages,
    HashSet<string> historyContent)
{
    var completedTurns = new List<ChatMessageModel>();
    var currentAssistantMessage = new ChatMessageModel { Role = "assistant" };

    if (bufferedMessages.Count == 0)
    {
        return (completedTurns, currentAssistantMessage);
    }

    // Process messages in sequence order
    var orderedMessages = bufferedMessages
        .OrderBy(m => m.SequenceNumber)
        .ToList();

    var needsReasoningSeparator = false;
    string? currentMessageId = null;

    foreach (var msg in orderedMessages)
    {
        // Handle user messages - they're always complete, add directly
        if (msg.UserMessage is not null)
        {
            // If we have pending assistant content, save it first
            if (currentAssistantMessage.HasContent)
            {
                var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
                if (strippedMessage.HasContent)
                {
                    completedTurns.Add(strippedMessage);
                }
                currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
                needsReasoningSeparator = false;
            }

            completedTurns.Add(new ChatMessageModel
            {
                Role = "user",
                Content = msg.Content ?? "",
                SenderId = msg.UserMessage.SenderId
            });
            continue;
        }

        // Handle assistant messages
        // If message ID changed and we have content, save the previous turn
        if (currentMessageId is not null && msg.MessageId != currentMessageId && currentAssistantMessage.HasContent)
        {
            var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
            if (strippedMessage.HasContent)
            {
                completedTurns.Add(strippedMessage);
            }
            currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
            needsReasoningSeparator = false;
        }

        currentMessageId = msg.MessageId;

        // Skip complete markers and errors for accumulation
        if (msg.IsComplete || msg.Error is not null)
        {
            if (msg.IsComplete && currentAssistantMessage.HasContent)
            {
                var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
                if (strippedMessage.HasContent)
                {
                    completedTurns.Add(strippedMessage);
                }
                currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
                needsReasoningSeparator = false;
            }
            continue;
        }

        currentAssistantMessage = AccumulateChunk(currentAssistantMessage, msg, ref needsReasoningSeparator);
    }

    var streamingMessage = StripKnownContent(currentAssistantMessage, historyContent);
    return (completedTurns, streamingMessage);
}
```

**Step 4: Run all BufferRebuildUtility tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~BufferRebuildUtilityTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/Services/Streaming/BufferRebuildUtility.cs Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs
git commit -m "feat(webchat): update BufferRebuildUtility to handle mixed user/assistant messages"
```

---

## Task 6: Update StreamResumeService to handle user messages from buffer

**Files:**
- Modify: `WebChat.Client/Services/Streaming/StreamResumeService.cs`

**Step 1: Review current implementation**

The current `StreamResumeService.TryResumeStreamAsync` already processes `completedTurns` from `BufferRebuildUtility.RebuildFromBuffer`. Since we updated `RebuildFromBuffer` to return user messages in `completedTurns`, the existing loop should work:

```csharp
foreach (var turn in completedTurns.Where(t => t.HasContent))
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, turn));
}
```

**Step 2: Verify by running existing tests**

Run: `dotnet test Tests/Tests.csproj`
Expected: All tests PASS

**Step 3: No code changes needed - the refactored BufferRebuildUtility handles it**

The `StreamResumeService` already dispatches `AddMessage` for each completed turn, and `ChatMessageModel` already has `Role` and `SenderId` properties that will be populated correctly by the updated `BufferRebuildUtility`.

**Step 4: Commit (documentation only if needed)**

No commit needed if no changes were made.

---

## Task 7: Run full test suite and fix any issues

**Step 1: Run full test suite**

Run: `dotnet test Tests/Tests.csproj`
Expected: All tests PASS

**Step 2: Fix any compilation errors or test failures**

Address any issues that arise.

**Step 3: Build entire solution**

Run: `dotnet build`
Expected: Build succeeded

**Step 4: Final commit if any fixes were needed**

```bash
git add -A
git commit -m "fix(webchat): address test failures in user message sync implementation"
```

---

## Task 8: Manual testing verification

**Step 1: Start the application**

Run: `dotnet run --project Agent/Agent.csproj`

**Step 2: Open two browser windows to the same topic**

1. Open browser A, navigate to WebChat
2. Open browser B, navigate to WebChat
3. In browser A, select/create a topic
4. In browser B, select the same topic

**Step 3: Test user message sync**

1. In browser A, send "Hello from browser A"
2. Verify browser B sees "Hello from browser A" appear
3. Wait for agent response
4. In browser A, send "Second message" while agent is still responding (if possible)
5. Verify browser B sees messages in correct chronological order

**Step 4: Test refresh scenario**

1. In browser A, send a message
2. Refresh browser B
3. Verify browser B still sees all messages in correct order after refresh

**Step 5: Document any issues found**

Note any bugs for follow-up fixes.
