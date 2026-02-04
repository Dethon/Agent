# ServiceBus-to-WebUI Streaming Design

## Problem Statement

When messages arrive via ServiceBus, the WebUI does not show real-time streaming. The warning log `"WriteMessage: topicId {TopicId} not found in _responseChannels"` appears, and responses are not displayed.

### Root Cause

The current implementation creates a **session** but not a **stream** when ServiceBus messages arrive:

```
ServiceBus Message → EnqueueReceivedMessageAsync → ChatPrompt with Source=ServiceBus
    ↓
ChatMonitor → CreateTopicIfNeededAsync → WebChatMessengerClient creates SESSION only
    ↓
Response arrives → ProcessResponseStreamAsync → finds topicId via sessionManager ✓
    ↓
WriteMessageAsync → streamManager lookup fails → WARNING "topicId not found"
```

The `streamManager` has no entry because streams are only created in `EnqueuePromptAndGetResponses` (when a WebUI user sends a message directly).

## Requirements

1. ServiceBus messages must stream to WebUI in real-time
2. Each ServiceBus `sourceId` maps to its own WebUI topic
3. WebUI clients must be notified when a ServiceBus conversation starts
4. No changes to existing WebUI-originated message flow

## Solution Design

### Approach

Modify `WebChatMessengerClient.CreateTopicIfNeededAsync` to create a stream when the source is NOT WebUI.

### File Changes

**File:** `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs`

**Method:** `CreateTopicIfNeededAsync` (lines 157-187)

### Implementation

After creating/finding the session, check if the source is external (not WebUI). If so:
1. Look up the `topicId` via `sessionManager.GetTopicIdByChatId()`
2. Create the stream via `streamManager.GetOrCreateStream()`
3. Track the pending prompt via `streamManager.TryIncrementPending()`
4. Notify WebUI clients via `hubNotifier.NotifyStreamChangedAsync(StreamChangeType.Started)`

### Code Changes

```csharp
public async Task<AgentKey> CreateTopicIfNeededAsync(
    MessageSource source,
    long? chatId,
    long? threadId,
    string? agentId,
    string? topicName,
    CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(agentId))
    {
        throw new ArgumentException("agentId is required for WebChat", nameof(agentId));
    }

    string? topicId = null;
    long actualChatId;
    long actualThreadId;

    if (threadId.HasValue && chatId.HasValue)
    {
        var existingTopic = await threadStateStore.GetTopicByChatIdAndThreadIdAsync(
            agentId, chatId.Value, threadId.Value, ct);

        if (existingTopic is not null)
        {
            sessionManager.StartSession(existingTopic.TopicId, existingTopic.AgentId,
                existingTopic.ChatId, existingTopic.ThreadId);
            topicId = existingTopic.TopicId;
            actualChatId = existingTopic.ChatId;
            actualThreadId = existingTopic.ThreadId;
        }
        else
        {
            actualChatId = chatId.Value;
            actualThreadId = await CreateThread(actualChatId, topicName ?? "External message", agentId, ct);
            topicId = sessionManager.GetTopicIdByChatId(actualChatId);
        }
    }
    else
    {
        actualChatId = chatId ?? GenerateChatId();
        actualThreadId = await CreateThread(actualChatId, topicName ?? "External message", agentId, ct);
        topicId = sessionManager.GetTopicIdByChatId(actualChatId);
    }

    // For non-WebUI sources, create stream and notify WebUI clients
    if (source != MessageSource.WebUi && topicId is not null)
    {
        streamManager.GetOrCreateStream(topicId, topicName ?? "", null, ct);
        streamManager.TryIncrementPending(topicId);

        await hubNotifier.NotifyStreamChangedAsync(
                new StreamChangedNotification(StreamChangeType.Started, topicId), ct)
            .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);
    }

    return new AgentKey(actualChatId, actualThreadId, agentId);
}
```

## Data Flow After Fix

```
ServiceBus Message → EnqueueReceivedMessageAsync → ChatPrompt with Source=ServiceBus
    ↓
ChatMonitor → CreateTopicIfNeededAsync
    ↓
WebChatMessengerClient:
  1. Creates/finds session ✓
  2. Creates stream (source != WebUi) ✓
  3. Increments pending count ✓
  4. Notifies WebUI (StreamChangeType.Started) ✓
    ↓
Response arrives → ProcessResponseStreamAsync
    ↓
WriteMessageAsync → streamManager has entry → SUCCESS ✓
    ↓
WebUI displays real-time streaming ✓
```

## Testing Strategy

1. **Unit test:** Verify `CreateTopicIfNeededAsync` creates stream for `MessageSource.ServiceBus`
2. **Unit test:** Verify `CreateTopicIfNeededAsync` does NOT create stream for `MessageSource.WebUi`
3. **Unit test:** Verify `StreamChangedNotification.Started` is sent for ServiceBus source
4. **Integration test:** Send message via ServiceBus, verify WebUI receives streaming response

## Files to Modify

1. `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` - Main fix
2. `Tests/Unit/Infrastructure/Messaging/WebChatMessengerClientTests.cs` - Add tests (if exists)

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Stream created but no response arrives (timeout) | Existing stream cleanup logic handles orphaned streams |
| Multiple rapid ServiceBus messages for same sourceId | `GetOrCreateStream` is idempotent, returns existing stream |
| WebUI client not connected when stream starts | Client can call `ResumeStream` or `GetStreamState` on connect |
