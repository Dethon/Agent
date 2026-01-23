# WebChat Concurrent Streaming Fix

## Problem

When a user sends a second message while the agent is still responding, the WebChat UI breaks:

- Each `SendMessage` call creates a new SignalR stream subscription
- The new subscription replaces the previous one in `WebChatStreamManager`
- The first subscriber becomes orphaned (receives no more messages)
- Frontend has two concurrent `StreamResponseAsync` calls fighting over the same `StreamingByTopic[topicId]` entry
- Message ordering gets corrupted in the UI

The backend's `ChatMonitor` correctly merges all prompts into a single response stream per topic, but the subscription mechanism doesn't support this pattern.

## Solution

Decouple stream subscription from prompt sending:

1. **Backend**: Track pending prompt count per topic. Only close the stream when all prompts complete.
2. **Backend**: Add `EnqueueMessage` hub method that enqueues without creating a new subscription.
3. **Frontend**: Reuse existing stream subscription when one is active. Only create a new subscription when no stream exists.
4. **Locking**: Backend uses locks to prevent race conditions between stream completion and new prompt enqueueing.

## Backend Changes

### WebChatStreamManager

Add pending prompt tracking with thread-safe locking:

```csharp
private readonly ConcurrentDictionary<string, int> _pendingPromptCounts = new();
private readonly object _streamLock = new();

public bool TryIncrementPending(string topicId)
{
    lock (_streamLock)
    {
        if (!_responseChannels.ContainsKey(topicId))
        {
            return false; // No active stream
        }

        _pendingPromptCounts.AddOrUpdate(topicId, 1, (_, count) => count + 1);
        return true;
    }
}

public bool DecrementPendingAndCompleteIfZero(string topicId)
{
    lock (_streamLock)
    {
        var newCount = _pendingPromptCounts.AddOrUpdate(topicId, 0, (_, count) => Math.Max(0, count - 1));

        if (newCount == 0)
        {
            CompleteStreamInternal(topicId);
            return true; // Stream completed
        }

        return false; // Stream still active
    }
}
```

Modify `CreateStream` to support reusing existing streams via `GetOrCreateStream`.

Clean up `_pendingPromptCounts` in `CleanupStreamState`.

### WebChatMessengerClient

Add `EnqueuePrompt` method:

```csharp
public bool EnqueuePrompt(string topicId, string message, string sender)
{
    if (!sessionManager.TryGetSession(topicId, out var session) || session is null)
    {
        return false;
    }

    if (!streamManager.TryIncrementPending(topicId))
    {
        return false; // Stream closed, frontend should create new subscription
    }

    var prompt = new ChatPrompt { /* ... */ };
    _promptChannel.Writer.TryWrite(prompt);
    return true;
}
```

Update `ProcessResponseStreamAsync` to use `DecrementPendingAndCompleteIfZero`:

```csharp
if (content is StreamCompleteContent)
{
    await streamManager.WriteMessageAsync(topicId,
        new ChatStreamMessage { IsComplete = true, MessageId = update.MessageId }, ct);

    if (streamManager.DecrementPendingAndCompleteIfZero(topicId))
    {
        await hubNotifier.NotifyStreamChangedAsync(
            new StreamChangedNotification(StreamChangeType.Completed, topicId), ct);
    }
    continue;
}
```

### ChatHub

Add new hub method:

```csharp
public bool EnqueueMessage(string topicId, string message)
{
    if (!IsRegistered) return false;
    if (!messengerClient.TryGetSession(topicId, out _)) return false;

    var userId = GetRegisteredUserId() ?? "Anonymous";
    return messengerClient.EnqueuePrompt(topicId, message, userId);
}
```

## Frontend Changes

### IChatMessagingService / ChatMessagingService

Add new method:

```csharp
// In IChatMessagingService
Task<bool> EnqueueMessageAsync(string topicId, string message);

// In ChatMessagingService
public async Task<bool> EnqueueMessageAsync(string topicId, string message)
{
    var hubConnection = connectionService.HubConnection;
    if (hubConnection is null) return false;

    return await hubConnection.InvokeAsync<bool>("EnqueueMessage", topicId, message);
}
```

### StreamingService

Track active stream tasks per topic with locking:

```csharp
private readonly ConcurrentDictionary<string, Task> _activeStreams = new();
private readonly SemaphoreSlim _streamLock = new(1, 1);

public async Task SendMessageAsync(StoredTopic topic, string message)
{
    await _streamLock.WaitAsync();
    try
    {
        var isNewStream = !_activeStreams.TryGetValue(topic.TopicId, out var task)
            || task.IsCompleted;

        if (isNewStream)
        {
            dispatcher.Dispatch(new StreamStarted(topic.TopicId));
            var streamTask = StreamResponseAsync(topic, message);
            _activeStreams[topic.TopicId] = streamTask;
            _ = streamTask.ContinueWith(_ => _activeStreams.TryRemove(topic.TopicId, out _));
        }
        else
        {
            var success = await messagingService.EnqueueMessageAsync(topic.TopicId, message);
            if (!success)
            {
                // Race: stream closed, fall back to new subscription
                dispatcher.Dispatch(new StreamStarted(topic.TopicId));
                var streamTask = StreamResponseAsync(topic, message);
                _activeStreams[topic.TopicId] = streamTask;
                _ = streamTask.ContinueWith(_ => _activeStreams.TryRemove(topic.TopicId, out _));
            }
        }
    }
    finally
    {
        _streamLock.Release();
    }
}
```

### SendMessageEffect

Simplify to delegate to StreamingService:

```csharp
private async Task HandleSendMessageAsync(SendMessage action)
{
    // ... topic creation/lookup logic stays the same ...

    // Add user message to store
    _dispatcher.Dispatch(new AddMessage(topic.TopicId, new ChatMessageModel { ... }));

    // Delegate to streaming service (handles stream reuse internally)
    await _streamingService.SendMessageAsync(topic, action.Message);
}
```

## Message Flow

### Scenario: User sends two messages while agent is responding

```
1. User sends "Hello"
   - Frontend: AddMessage(user: "Hello")
   - Frontend: No active stream -> StreamStarted, call SendMessage hub
   - Backend: GetOrCreateStream (creates new), TryIncrementPending (count=1)
   - Backend: Enqueue prompt to ChatMonitor
   - Frontend: StreamResponseAsync starts consuming

2. User sends "What about X?" (while agent still responding)
   - Frontend: AddMessage(user: "What about X?")
   - Frontend: Active stream exists -> call EnqueueMessage hub only
   - Backend: TryIncrementPending (count=2) under lock, returns true
   - Backend: Enqueue prompt to ChatMonitor

3. Backend: Agent finishes responding to "Hello"
   - Backend: StreamCompleteContent received
   - Backend: WriteMessage(IsComplete=true), DecrementPendingAndCompleteIfZero (count=1)
   - Backend: count > 0, stream stays open
   - Frontend: Receives IsComplete, finalizes first assistant message

4. Backend: Agent finishes responding to "What about X?"
   - Backend: StreamCompleteContent received
   - Backend: WriteMessage(IsComplete=true), DecrementPendingAndCompleteIfZero (count=0)
   - Backend: count == 0, CompleteStream + notify
   - Frontend: Receives IsComplete, finalizes second assistant message
   - Frontend: Stream ends, StreamResponseAsync exits

5. User sends "Thanks"
   - Frontend: No active stream -> creates new subscription
```

## Edge Cases

### Stream Cancellation

- `CancelStream` resets `_pendingPromptCounts[topicId]` to 0
- Frontend `_activeStreams` entry removed when `StreamResponseAsync` exits

### Error During Processing

- Catch block calls `DecrementPendingAndCompleteIfZero`
- Stream completed if count reaches 0

### Race Condition: Message sent as stream completes

- Backend lock ensures `TryIncrementPending` is atomic with stream existence check
- If stream just closed, returns `false`
- Frontend catches `false` and creates new subscription

## Files to Modify

### Backend

| File | Changes |
|------|---------|
| `Infrastructure/Clients/Messaging/WebChatStreamManager.cs` | Add `_pendingPromptCounts`, `_streamLock`, `TryIncrementPending`, `DecrementPendingAndCompleteIfZero` |
| `Infrastructure/Clients/Messaging/WebChatMessengerClient.cs` | Add `EnqueuePrompt` method, update `ProcessResponseStreamAsync` |
| `Agent/Hubs/ChatHub.cs` | Add `EnqueueMessage` hub method returning `bool` |

### Frontend

| File | Changes |
|------|---------|
| `WebChat.Client/Contracts/IChatMessagingService.cs` | Add `EnqueueMessageAsync` |
| `WebChat.Client/Services/ChatMessagingService.cs` | Implement `EnqueueMessageAsync` |
| `WebChat.Client/Contracts/IStreamingService.cs` | Add/update `SendMessageAsync` signature |
| `WebChat.Client/Services/Streaming/StreamingService.cs` | Add `_activeStreams`, `_streamLock`, stream reuse logic |
| `WebChat.Client/State/Effects/SendMessageEffect.cs` | Simplify to delegate to `StreamingService.SendMessageAsync` |
