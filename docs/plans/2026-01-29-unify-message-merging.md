# Unify Message Merging Into the View Layer

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove duplicated merge logic from StreamingService by making it always finalize every message turn to the store, and letting the view layer (MessageList + StreamingMessageDisplay) handle all merging.

**Architecture:** StreamingService stops special-casing content-less turns (no more CarryForward/TrimSeparators). MessageList.razor detects trailing content-less assistant messages during streaming and passes their reasoning/toolcalls to StreamingMessageDisplay as "carried" context. StreamingMessageDisplay prepends carried context when rendering. Performance is preserved — finalized messages only re-render on store changes, streaming bubble still updates at 50ms.

**Tech Stack:** .NET 10, Blazor WASM, Fluxor-style state management

---

### Task 1: Add failing tests for content-less turn finalization

**Files:**
- Modify: `Tests/Unit/WebChat/Client/StreamingServiceTests.cs`

**Step 1: Write failing tests**

Add after the existing `StreamResponseAsync_MultiTurn_SeparatesTurns` test (~line 206):

```csharp
[Fact]
public async Task StreamResponseAsync_ReasoningOnlyTurn_FinalizesToStore()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    _messagingService.EnqueueMessages(
        new ChatStreamMessage { Reasoning = "Thinking step", MessageId = "msg-1" },
        new ChatStreamMessage { Content = "Final answer", MessageId = "msg-2" },
        new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" });

    await _service.StreamResponseAsync(topic, "test");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.Count.ShouldBe(2);
    messages[0].Reasoning.ShouldBe("Thinking step");
    messages[0].Content.ShouldBeEmpty();
    messages[1].Content.ShouldBe("Final answer");
}

[Fact]
public async Task StreamResponseAsync_ToolCallsOnlyTurn_FinalizesToStore()
{
    var topic = CreateTopic();
    _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
    _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

    _messagingService.EnqueueMessages(
        new ChatStreamMessage { ToolCalls = "search(\"query\")", MessageId = "msg-1" },
        new ChatStreamMessage { Content = "Found results", MessageId = "msg-2" },
        new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" });

    await _service.StreamResponseAsync(topic, "test");

    var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
    messages.Count.ShouldBe(2);
    messages[0].ToolCalls.ShouldBe("search(\"query\")");
    messages[0].Content.ShouldBeEmpty();
    messages[1].Content.ShouldBe("Found results");
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "ReasoningOnlyTurn_FinalizesToStore|ToolCallsOnlyTurn_FinalizesToStore"`

Expected: FAIL — CarryForward currently prevents content-less turns from being finalized (merges them into the next turn, producing 1 message instead of 2).

**Step 3: Commit**

```
test(webchat): add failing tests for content-less turn finalization
```

---

### Task 2: Remove CarryForward/TrimSeparators from StreamingService

**Files:**
- Modify: `WebChat.Client/Services/Streaming/StreamingService.cs`

**Step 1: Delete CarryForward, TrimSeparators, and ReasoningSeparator**

Remove lines 89-115 (the constant and both methods).

**Step 2: Simplify isNewMessageTurn in StreamResponseAsync**

Replace lines 173-185 (the `if/else` with Content check and CarryForward) with:

```csharp
if (isNewMessageTurn && streamingMessage.HasContent)
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage, currentMessageId));
    streamingMessage = new ChatMessageModel { Role = "assistant" };
    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));
}
```

Every turn with HasContent gets finalized unconditionally — no Content vs no-Content branching.

**Step 3: Remove TrimSeparators from final AddMessage in StreamResponseAsync**

Change line ~212 from `TrimSeparators(streamingMessage)` to just `streamingMessage`:

```csharp
if (streamingMessage.HasContent)
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage, currentMessageId));
}
```

**Step 4: Apply same changes in ResumeStreamResponseAsync**

Replace lines ~299-311 with the same simplified pattern (keep the processedLength resets):

```csharp
if (isNewMessageTurn && streamingMessage.HasContent)
{
    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage, currentMessageId));
    streamingMessage = new ChatMessageModel { Role = "assistant" };
    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));

    processedContentLength = 0;
    processedReasoningLength = 0;
    processedToolCallsLength = 0;
}
```

And change final AddMessage at line ~365 to remove TrimSeparators.

**Step 5: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~StreamingServiceTests"`

Expected: All pass, including the two new tests from Task 1.

**Step 6: Commit**

```
refactor(webchat): remove CarryForward/TrimSeparators from StreamingService
```

---

### Task 3: Add CarriedReasoning/CarriedToolCalls to StreamingMessageDisplay

**Files:**
- Modify: `WebChat.Client/Components/Chat/StreamingMessageDisplay.razor`

**Step 1: Add parameters and merge logic**

Add two parameters and update rendering:

```razor
[Parameter] public string? CarriedReasoning { get; set; }
[Parameter] public string? CarriedToolCalls { get; set; }
```

Update `HasDisplayableContent`:

```csharp
private bool HasDisplayableContent()
{
    return !string.IsNullOrEmpty(_streamingContent?.Content) ||
           !string.IsNullOrEmpty(_streamingContent?.Reasoning) ||
           !string.IsNullOrEmpty(_streamingContent?.ToolCalls) ||
           !string.IsNullOrEmpty(CarriedReasoning) ||
           !string.IsNullOrEmpty(CarriedToolCalls);
}
```

Update `CreateMessageModel` to prepend carried context:

```csharp
private ChatMessageModel CreateMessageModel() => new()
{
    Role = "assistant",
    Content = _streamingContent?.Content ?? "",
    Reasoning = MergeField(CarriedReasoning, _streamingContent?.Reasoning, "\n-----\n"),
    ToolCalls = MergeField(CarriedToolCalls, _streamingContent?.ToolCalls, "\n"),
    IsError = _streamingContent?.IsError ?? false
};

private static string? MergeField(string? carried, string? live, string separator) =>
    (carried, live) switch
    {
        (null or "", null or "") => null,
        (null or "", _) => live,
        (_, null or "") => carried,
        _ => carried + separator + live
    };
```

**Step 2: Verify build**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`

Expected: Builds. Purely additive — new params default to null, existing behavior unchanged.

**Step 3: Commit**

```
feat(webchat): add CarriedReasoning/CarriedToolCalls to StreamingMessageDisplay
```

---

### Task 4: Absorb trailing content-less messages in MessageList.razor

**Files:**
- Modify: `WebChat.Client/Components/Chat/MessageList.razor`

**Step 1: Add carried fields and update UpdateMessages**

Add fields in `@code` block:

```csharp
private string? _carriedReasoning;
private string? _carriedToolCalls;
```

Replace `UpdateMessages` (lines 61-67):

```csharp
private void UpdateMessages()
{
    var raw = _topicId != null
        ? MessagesStore.State.MessagesByTopic.GetValueOrDefault(_topicId, [])
        : [];
    var merged = MergeConsecutiveAssistantMessages(raw);

    if (_isStreaming && merged.Count > 0
        && merged[^1] is { Role: "assistant", Content: "" or null } last
        && (last.Reasoning is not null || last.ToolCalls is not null))
    {
        _carriedReasoning = last.Reasoning;
        _carriedToolCalls = last.ToolCalls;
        _messages = merged.Take(merged.Count - 1).ToList();
    }
    else
    {
        _carriedReasoning = null;
        _carriedToolCalls = null;
        _messages = merged;
    }
}
```

**Step 2: Re-merge when streaming stops**

Update `UpdateStreamingStatus` (line ~154-157):

```csharp
private void UpdateStreamingStatus()
{
    var wasStreaming = _isStreaming;
    _isStreaming = _topicId != null && StreamingStore.State.StreamingTopics.Contains(_topicId);

    if (wasStreaming && !_isStreaming)
    {
        UpdateMessages();
    }
}
```

**Step 3: Pass carried params to StreamingMessageDisplay**

Update the template (line ~254-257):

```razor
@if (_isStreaming && !string.IsNullOrEmpty(_topicId))
{
    <StreamingMessageDisplay TopicId="@_topicId"
                             CarriedReasoning="@_carriedReasoning"
                             CarriedToolCalls="@_carriedToolCalls"/>
}
```

**Step 4: Verify build**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`

**Step 5: Commit**

```
feat(webchat): absorb trailing content-less messages into streaming bubble
```

---

### Task 5: Add unit tests for view-level merge

**Files:**
- Modify: `WebChat.Client/Components/Chat/MessageList.razor` (make method `internal static`)
- Create: `Tests/Unit/WebChat/Client/MessageMergeTests.cs`

**Step 1: Make MergeConsecutiveAssistantMessages accessible**

Change the method signature in MessageList.razor from `private static` to `internal static`:

```csharp
internal static IReadOnlyList<ChatMessageModel> MergeConsecutiveAssistantMessages(IReadOnlyList<ChatMessageModel> messages)
```

`InternalsVisibleTo("Tests")` already exists in WebChat.Client.csproj.

**Step 2: Write tests**

Create `Tests/Unit/WebChat/Client/MessageMergeTests.cs`:

```csharp
using Shouldly;
using WebChat.Client.Components.Chat;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client;

public sealed class MessageMergeTests
{
    [Fact]
    public void ReasoningOnly_FollowedByContent_MergesIntoOne()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "", Reasoning = "Thinking" },
            new() { Role = "assistant", Content = "Answer" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("Answer");
        result[0].Reasoning.ShouldBe("Thinking");
    }

    [Fact]
    public void MultipleReasoningBlocks_JoinedWithSeparator()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "", Reasoning = "Step 1" },
            new() { Role = "assistant", Content = "Answer", Reasoning = "Step 2" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(1);
        result[0].Reasoning.ShouldBe("Step 1\n-----\nStep 2");
    }

    [Fact]
    public void ToolCallsOnly_FollowedByContent_MergesToolCalls()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "", ToolCalls = "tool_1" },
            new() { Role = "assistant", Content = "Result", ToolCalls = "tool_2" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("Result");
        result[0].ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public void TrailingContentLess_PreservedWhenAlone()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "", Reasoning = "Thinking" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(2);
        result[1].Reasoning.ShouldBe("Thinking");
        result[1].Content.ShouldBeEmpty();
    }

    [Fact]
    public void NonAssistantBreaksRun()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "First" },
            new() { Role = "user", Content = "Question" },
            new() { Role = "assistant", Content = "Second" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(3);
    }
}
```

**Step 3: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageMergeTests"`

Expected: All pass (the merge algorithm already works this way).

**Step 4: Commit**

```
test(webchat): add unit tests for MergeConsecutiveAssistantMessages
```

---

### Task 6: Update documentation

**Files:**
- Modify: `docs/message-merging.md`

**Step 1: Rewrite to reflect unified architecture**

Replace the content to describe:
- StreamingService always finalizes every turn (no special-casing)
- MessageList.razor merges consecutive assistant messages + absorbs trailing content-less into streaming bubble
- StreamingMessageDisplay prepends carried reasoning/toolcalls
- Single conceptual merge point (view layer), no duplication

**Step 2: Commit**

```
docs: update message-merging.md for unified view-layer merge
```

---

### Task 7: Full test suite verification

**Step 1: Run all tests**

Run: `dotnet test Tests/Tests.csproj`

Pay attention to:
- `StreamingServiceTests` — all existing + new tests
- `StreamingServiceIntegrationTests` — integration tests with real server
- `StreamResumeServiceTests` — buffer resume path
- `MessageMergeTests` — new merge algorithm tests

**Step 2: Build entire solution**

Run: `dotnet build`

---

## Verification

1. Run `dotnet test Tests/Tests.csproj` — all tests pass
2. Run `dotnet build` — solution builds
3. Manual test: start a streaming response that produces a reasoning-only turn followed by a content turn. Verify no empty bubble flashes during streaming and the final result shows reasoning collapsed above the content.
