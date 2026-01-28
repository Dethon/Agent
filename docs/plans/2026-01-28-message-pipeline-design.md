# Message Pipeline Design

## Problem Statement

The WebChat client has five message sources (SendMessageEffect, StreamingService, StreamResumeService, HubEventDispatcher, TopicSelectionEffect) with four separate deduplication mechanisms scattered across the codebase. This causes:

1. **Debugging difficulty** - Hard to trace why messages appear/disappear or get duplicated
2. **Race condition bugs** - Messages sometimes duplicate or finalize incorrectly
3. **Code maintainability** - Scattered logic makes the codebase hard to understand

## Solution: Unified Message Pipeline

Introduce a `MessagePipeline` service that becomes the single owner of all message state transitions. Instead of five sources dispatching actions directly to stores, they all call the pipeline.

### Core Concept

**Current flow:**
```
Source -> Action -> Reducer -> Store -> UI
         ^ dedup logic scattered everywhere
```

**New flow:**
```
Source -> MessagePipeline -> Action -> Reducer -> Store -> UI
              ^ all dedup and accumulation here
```

The pipeline maintains internal state keyed by MessageId (or correlation ID). When a source calls the pipeline, it either:
- Creates a new message state entry
- Updates an existing one (accumulate content, transition state)
- Ignores duplicates (already finalized)

## Message States

```csharp
public enum MessageLifecycle
{
    Pending,      // User message created, awaiting server confirmation
    Streaming,    // Assistant message receiving chunks
    Finalized     // Complete, in history
}

public record ManagedMessage
{
    public string Id { get; init; }              // MessageId or correlation ID
    public string TopicId { get; init; }
    public MessageLifecycle State { get; init; }
    public string Role { get; init; }            // "user" or "assistant"
    public string Content { get; init; }
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? SenderId { get; init; }
}
```

## Pipeline Interface

```csharp
public interface IMessagePipeline
{
    // User sends a message
    string SubmitUserMessage(string topicId, string content, string senderId);

    // Streaming chunks arrive
    void AccumulateChunk(string topicId, string? messageId,
        string? content, string? reasoning, string? toolCalls);

    // Message complete (turn ended or stream finished)
    void FinalizeMessage(string topicId, string? messageId);

    // Load history (topic selection / init)
    void LoadHistory(string topicId, IEnumerable<ChatHistoryMessage> messages);

    // Buffer resume - accepts pre-accumulated buffer
    void ResumeFromBuffer(string topicId, IEnumerable<BufferedMessage> buffer,
        string? currentMessageId);

    // Reset on error or cancel
    void Reset(string topicId);
}
```

## Source Integration

### 1. SendMessageEffect (user sends message)
```csharp
// Before: dispatches AddMessage, tracks correlation, calls StreamingService
// After:
var messageId = _pipeline.SubmitUserMessage(topicId, content, senderId);
await _streamingService.SendMessageAsync(topicId, content);
```

### 2. StreamingService (real-time chunks)
```csharp
// Before: AccumulateChunk locally, dispatch StreamChunk, handle turn boundaries
// After:
await foreach (var chunk in hubStream)
{
    _pipeline.AccumulateChunk(topicId, chunk.MessageId,
        chunk.Content, chunk.Reasoning, chunk.ToolCalls);

    if (chunk.IsUserMessage)
        _pipeline.FinalizeMessage(topicId, previousMessageId);
}
_pipeline.FinalizeMessage(topicId, currentMessageId);
```

### 3. StreamResumeService (buffer reconnection)
```csharp
// Before: RebuildFromBuffer, StripKnownContent, dispatch multiple actions
// After:
_pipeline.ResumeFromBuffer(topicId, streamState.BufferedMessages,
    streamState.CurrentMessageId);
```

### 4. HubEventDispatcher (other users' messages)
```csharp
// Before: check SentMessageTracker, dispatch AddMessage, handle finalization
// After:
_pipeline.AccumulateChunk(topicId, messageId, content, null, null);
_pipeline.FinalizeMessage(topicId, messageId);
```

### 5. TopicSelectionEffect / InitializationEffect (history)
```csharp
// Before: dispatch MessagesLoaded
// After:
_pipeline.LoadHistory(topicId, historyMessages);
```

## Consolidated Deduplication

All four deduplication mechanisms merge into the pipeline:

```csharp
public class MessagePipeline : IMessagePipeline
{
    // All managed messages by ID
    private readonly Dictionary<string, ManagedMessage> _messagesById = new();

    // Finalized message IDs per topic (replaces FinalizedMessageIdsByTopic in store)
    private readonly Dictionary<string, HashSet<string>> _finalizedByTopic = new();

    // Pending user messages by correlation ID (replaces SentMessageTracker)
    private readonly Dictionary<string, string> _pendingUserMessages = new();

    // Current streaming message per topic
    private readonly Dictionary<string, string> _streamingByTopic = new();

    private bool ShouldProcess(string topicId, string? messageId)
    {
        // No ID yet = always process (will get ID later)
        if (string.IsNullOrEmpty(messageId))
            return true;

        // Already finalized = skip
        if (_finalizedByTopic.TryGetValue(topicId, out var finalized)
            && finalized.Contains(messageId))
            return false;

        // Already tracking = update existing (not duplicate)
        return true;
    }
}
```

**What gets deleted:**
- `SentMessageTracker` class - absorbed into `_pendingUserMessages`
- `FinalizedMessageIdsByTopic` in MessagesState - moved to pipeline
- `StripKnownContent` / `StripKnownContentById` - no longer needed
- `FinalizationRequests` flags - no more race conditions

## Debugging and Tracing

The pipeline provides observability for all message flow:

```csharp
public void AccumulateChunk(string topicId, string? messageId, ...)
{
    if (_logger.IsEnabled(LogLevel.Debug))
    {
        _logger.LogDebug(
            "Pipeline.AccumulateChunk: topic={TopicId}, messageId={MessageId}, " +
            "contentLen={ContentLen}, state={State}, source={Source}",
            topicId, messageId, content?.Length ?? 0,
            GetCurrentState(topicId, messageId), GetCallerSource());
    }

    // ... actual logic
}
```

**Lifecycle events observable:**
```csharp
public IObservable<MessageLifecycleEvent> LifecycleEvents { get; }

public record MessageLifecycleEvent(
    string TopicId,
    string MessageId,
    MessageLifecycle FromState,
    MessageLifecycle ToState,
    string Source,           // Which service triggered this
    DateTimeOffset Timestamp
);
```

**Debug snapshot:**
```csharp
public PipelineSnapshot GetSnapshot(string topicId)
{
    return new PipelineSnapshot(
        StreamingMessageId: _streamingByTopic.GetValueOrDefault(topicId),
        FinalizedCount: _finalizedByTopic.GetValueOrDefault(topicId)?.Count ?? 0,
        PendingUserMessages: _pendingUserMessages.Count,
        ActiveMessages: _messagesById.Values
            .Where(m => m.TopicId == topicId)
            .ToList()
    );
}
```

## Store Simplification

With the pipeline owning state, stores become simpler:

**New actions (replacing scattered ones):**
```csharp
public record SetMessages(string TopicId, IReadOnlyList<ChatMessageModel> Messages);
public record SetStreamingContent(string TopicId, StreamingContent? Content);
public record AppendMessage(string TopicId, ChatMessageModel Message);
```

**Simplified reducer:**
```csharp
// Before: complex dedup logic
// After:
case AppendMessage action:
    var messages = state.MessagesByTopic.GetValueOrDefault(action.TopicId)
        ?? ImmutableList<ChatMessageModel>.Empty;
    return state with {
        MessagesByTopic = state.MessagesByTopic.SetItem(
            action.TopicId, messages.Add(action.Message))
    };
```

## Migration Plan

### Phase 1: Introduce pipeline alongside existing code
- Create `MessagePipeline` class with the interface
- Pipeline dispatches the existing actions initially
- No behavior change yet - just a new layer

### Phase 2: Migrate sources one at a time
- Start with `LoadHistory` (simplest, no streaming)
- Then `SubmitUserMessage`
- Then `AccumulateChunk` + `FinalizeMessage`
- Finally `ResumeFromBuffer` (most complex)
- Each migration is independently testable

### Phase 3: Move deduplication into pipeline
- Once all sources use pipeline, move dedup logic in
- Delete `SentMessageTracker`
- Remove `FinalizedMessageIdsByTopic` from store
- Remove `FinalizationRequests` flags

### Phase 4: Simplify stores
- Replace complex reducers with simple setters
- Delete dead code (`BufferRebuildUtility.StripKnownContent`, etc.)

### Testing Strategy
- Unit test the pipeline in isolation (no Blazor, no SignalR)
- Each phase should pass existing integration tests
- Add pipeline-specific tests for edge cases

### Rollback Safety
- Phase 1-2: Can remove pipeline and revert to direct dispatch
- Phase 3+: Committed to new architecture, but each step is small

## Files Affected

### New Files
- `WebChat.Client/State/Pipeline/IMessagePipeline.cs`
- `WebChat.Client/State/Pipeline/MessagePipeline.cs`
- `WebChat.Client/State/Pipeline/ManagedMessage.cs`
- `WebChat.Client/State/Pipeline/MessageLifecycleEvent.cs`

### Modified Files
- `WebChat.Client/State/Effects/SendMessageEffect.cs` - Use pipeline
- `WebChat.Client/State/Streaming/StreamingService.cs` - Use pipeline
- `WebChat.Client/State/Streaming/StreamResumeService.cs` - Use pipeline
- `WebChat.Client/State/Hub/HubEventDispatcher.cs` - Use pipeline
- `WebChat.Client/State/Effects/TopicSelectionEffect.cs` - Use pipeline
- `WebChat.Client/State/Effects/InitializationEffect.cs` - Use pipeline
- `WebChat.Client/State/Messages/MessagesReducers.cs` - Simplify
- `WebChat.Client/State/Streaming/StreamingReducers.cs` - Simplify
- `WebChat.Client/State/Messages/MessagesState.cs` - Remove FinalizedMessageIdsByTopic
- `WebChat.Client/State/Streaming/StreamingState.cs` - Remove FinalizationRequests

### Deleted Files (Phase 3+)
- `WebChat.Client/State/Tracking/SentMessageTracker.cs`
- Parts of `WebChat.Client/State/Streaming/BufferRebuildUtility.cs` (StripKnownContent methods)
