# Phase 4: SignalR Integration - Research

**Researched:** 2026-01-20
**Domain:** SignalR hub events, state management integration, subscription lifecycle
**Confidence:** HIGH

## Summary

Phase 4 routes SignalR hub events through the unidirectional state pattern established in Phases 1-3. The current implementation has `SignalREventSubscriber` calling `IChatNotificationHandler` which directly mutates `ChatStateManager` - this needs to transition to dispatching actions through the `Dispatcher` to update stores.

The key architectural shift is creating a `HubEventDispatcher` that transforms SignalR events into typed actions. This bridges the event-driven SignalR world with the action/reducer pattern. Reconnection logic needs to preserve streaming state and resume properly. Event subscription lifecycle must be managed to prevent memory leaks.

**Primary recommendation:** Create `HubEventDispatcher` that receives SignalR notifications and dispatches corresponding actions; replace `ChatNotificationHandler` direct state mutations with action dispatches.

## Standard Stack

The architecture uses existing patterns from Phases 1-3:

### Core (Already Established)
| Component | Purpose | Location |
|-----------|---------|----------|
| `Dispatcher` | Routes actions to store handlers | `State/Dispatcher.cs` |
| `IDispatcher` | Interface for component injection | `State/IDispatcher.cs` |
| `Store<TState>` | BehaviorSubject-based reactive store | `State/Store.cs` |
| `IAction` | Marker interface for all actions | `State/IAction.cs` |
| `StoreSubscriberComponent` | Base component with subscription management | `State/StoreSubscriberComponent.cs` |

### SignalR (Already Established)
| Component | Purpose | Location |
|-----------|---------|----------|
| `ChatConnectionService` | SignalR connection lifecycle | `Services/ChatConnectionService.cs` |
| `SignalREventSubscriber` | Registers hub event handlers | `Services/SignalREventSubscriber.cs` |
| `ChatNotificationHandler` | Processes hub notifications | `Services/Handlers/ChatNotificationHandler.cs` |
| `AggressiveRetryPolicy` | Reconnection with exponential backoff | `Services/ChatConnectionService.cs` |

### New Components for Phase 4
| Component | Purpose | Pattern |
|-----------|---------|---------|
| `HubEventDispatcher` | Transforms SignalR events to actions | Bridge pattern |
| `IHubEventDispatcher` | Interface for testability | DI interface |

## Architecture Patterns

### Pattern 1: HubEventDispatcher as Event Bridge

**What:** A service that receives SignalR hub notifications and dispatches corresponding actions to the store system.

**Why:** Separates SignalR event handling (infrastructure concern) from state management (application concern). Enables testing of event-to-action mapping independently.

**Structure:**
```
SignalR Hub → SignalREventSubscriber → HubEventDispatcher → Dispatcher → Stores
                  (registers .On())       (maps to actions)   (routes)   (reduce)
```

**Example:**
```csharp
// Source: Existing pattern from SignalREventSubscriber + new HubEventDispatcher
public sealed class HubEventDispatcher(IDispatcher dispatcher) : IHubEventDispatcher
{
    public void HandleTopicChanged(TopicChangedNotification notification)
    {
        var action = notification.ChangeType switch
        {
            TopicChangeType.Created when notification.Topic is not null
                => (IAction)new AddTopic(StoredTopic.FromMetadata(notification.Topic)),
            TopicChangeType.Updated when notification.Topic is not null
                => new UpdateTopic(StoredTopic.FromMetadata(notification.Topic)),
            TopicChangeType.Deleted
                => new RemoveTopic(notification.TopicId),
            _ => throw new ArgumentOutOfRangeException()
        };
        dispatcher.Dispatch(action);
    }

    public void HandleStreamChanged(StreamChangedNotification notification)
    {
        var action = notification.ChangeType switch
        {
            StreamChangeType.Started => (IAction)new StreamStarted(notification.TopicId),
            StreamChangeType.Cancelled => new StreamCancelled(notification.TopicId),
            StreamChangeType.Completed => new StreamCompleted(notification.TopicId),
            _ => throw new ArgumentOutOfRangeException()
        };
        dispatcher.Dispatch(action);
    }
}
```

### Pattern 2: Reconnection State Preservation

**What:** When SignalR reconnects, preserve local state and resume active streams from the server-side buffer.

**Current behavior:** `ChatContainer.HandleReconnected()` loops through all topics and calls `StreamResumeService.TryResumeStreamAsync()`.

**Required enhancement:** Dispatch `ConnectionReconnected` action which triggers reconnection effects:
1. Restart session for selected topic
2. Resume streams for all topics that were streaming
3. Optionally refresh stale data

**Key insight:** Server buffers messages for 5 days (per CONTEXT.md), so client can catch up by requesting buffered content on reconnect.

### Pattern 3: Subscription Lifecycle Management

**What:** Ensure SignalR event handlers are registered once and not duplicated.

**Current implementation:** `SignalREventSubscriber` uses a `_subscribed` boolean flag to prevent double registration.

**Enhancement needed:** Make subscription idempotent and tie to component lifecycle. Currently `Subscribe()` is called in `ChatContainer.OnInitializedAsync()` but there's no unsubscribe on disposal.

**Note:** `HubConnection.On()` returns `IDisposable` which should be tracked and disposed when appropriate.

### Pattern 4: Per-Topic vs Global Event Routing

**What:** Some events affect all topics (topic list changes, connection status), others affect specific topics (streaming content, approvals).

**CONTEXT.md decisions:**
- Per-topic dispatchers for the active topic
- Lightweight listener across all topics for unread badge updates

**Implementation approach:**
- Global HubEventDispatcher handles all incoming SignalR events
- Events are transformed to actions with TopicId
- Stores filter by TopicId where relevant (already implemented in StreamingState/MessagesState)
- Unread counts computed via selectors (TopicsSelectors already exist)

### Anti-Patterns to Avoid

- **Double subscription:** Registering `.On()` handlers multiple times creates duplicate events
- **Sync-over-async:** SignalR handlers must be async; blocking can deadlock
- **State mutation in handlers:** Handlers should dispatch actions, not modify state directly
- **Orphaned subscriptions:** Not disposing `.On()` handlers when no longer needed

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Reconnection | Custom WebSocket retry | `WithAutomaticReconnect()` + `IRetryPolicy` | SignalR handles state, buffering, resubscription |
| Event deduplication | Custom sequence tracking | Server-side message buffering | Server already buffers; client requests missed messages |
| Thread marshaling | `Dispatcher.BeginInvoke` | `InvokeAsync()` in Blazor | Built into component base class |
| Subscription cleanup | Manual list tracking | `CompositeDisposable` from Rx.NET | Already used in `StoreSubscriberComponent` |

## Common Pitfalls

### Pitfall 1: SignalR Handler Registration Timing

**What goes wrong:** Calling `.On()` before connection is established, or calling after connection is disposed.

**Why it happens:** Component initialization order in Blazor is not guaranteed.

**How to avoid:**
- Check `HubConnection is not null` before registering (already done in `SignalREventSubscriber`)
- Register handlers after `StartAsync()` completes
- Store `IDisposable` returns from `.On()` calls

**Warning signs:** Events not firing, "connection disposed" exceptions

### Pitfall 2: Duplicate Event Processing

**What goes wrong:** Same event processed twice causing duplicate state updates.

**Why it happens:** Multiple calls to `Subscribe()` without cleanup, or reconnection re-registering handlers.

**How to avoid:**
- Use boolean flag pattern (current implementation)
- Track `IDisposable` from `.On()` and dispose before re-registering
- Make handlers idempotent where possible

**Warning signs:** Duplicate messages in UI, topics appearing twice

### Pitfall 3: Blocking in SignalR Handlers

**What goes wrong:** UI freezes, connection times out, cascading failures.

**Why it happens:** Synchronous operations in async handlers, awaiting on UI thread from background thread.

**How to avoid:**
- All handler callbacks should be `async`
- Use fire-and-forget (`_ = Task.Run(...)`) for long operations (already done in `ChatNotificationHandler.HandleStreamChangedAsync`)
- Don't await dispatcher operations if they might block

**Warning signs:** Connection drops, slow event processing, deadlocks

### Pitfall 4: Memory Leaks from Event Subscriptions

**What goes wrong:** Handlers keep component alive after disposal, memory grows over time.

**Why it happens:** `.On()` returns `IDisposable` that holds reference to handler delegate.

**How to avoid:**
- Store and dispose subscription handles
- Verify disposal in browser dev tools (Heap snapshots)
- Weak event pattern if needed (rarely necessary)

**Warning signs:** Growing memory in dev tools, "disposed" component still receiving events

### Pitfall 5: Race Conditions During Reconnection

**What goes wrong:** Reconnection starts before previous disconnect cleanup completes.

**Why it happens:** `Reconnecting` and `Reconnected` events fire in quick succession.

**How to avoid:**
- Use locking or state machine for connection state
- Dispatch actions sequentially (Dispatcher already synchronous)
- Check current state before processing events

**Warning signs:** Inconsistent state after reconnect, duplicate sessions

## Code Examples

### Current SignalR Event Flow (to be replaced)

```csharp
// Source: WebChat.Client/Services/SignalREventSubscriber.cs
hubConnection.On<TopicChangedNotification>("OnTopicChanged", async notification =>
{
    await notificationHandler.HandleTopicChangedAsync(notification);
});
```

### Current Direct State Mutation (to be replaced)

```csharp
// Source: WebChat.Client/Services/Handlers/ChatNotificationHandler.cs
public Task HandleTopicChangedAsync(TopicChangedNotification notification)
{
    switch (notification.ChangeType)
    {
        case TopicChangeType.Created when notification.Topic is not null:
            if (stateManager.Topics.All(t => t.TopicId != notification.TopicId))
            {
                var newTopic = StoredTopic.FromMetadata(notification.Topic);
                stateManager.AddTopic(newTopic);  // Direct mutation - to be replaced
            }
            break;
        // ...
    }
    return Task.CompletedTask;
}
```

### Target Pattern: Action Dispatch

```csharp
// Target: HubEventDispatcher dispatches actions instead
public void HandleTopicChanged(TopicChangedNotification notification)
{
    var action = notification.ChangeType switch
    {
        TopicChangeType.Created when notification.Topic is not null
            => (IAction)new AddTopic(StoredTopic.FromMetadata(notification.Topic)),
        TopicChangeType.Updated when notification.Topic is not null
            => new UpdateTopic(StoredTopic.FromMetadata(notification.Topic)),
        TopicChangeType.Deleted
            => new RemoveTopic(notification.TopicId),
        _ => throw new ArgumentOutOfRangeException()
    };
    dispatcher.Dispatch(action);
}
```

### Reconnection Handler Pattern

```csharp
// Source: WebChat.Client/Components/Chat/ChatContainer.razor (lines 110-121)
private async Task HandleReconnected()
{
    if (StateManager.SelectedTopic is not null)
    {
        await SessionService.StartSessionAsync(StateManager.SelectedTopic);
    }

    foreach (var topic in StateManager.Topics)
    {
        _ = StreamResumeService.TryResumeStreamAsync(topic);
    }
}
```

### Subscription Disposal Pattern

```csharp
// Source: WebChat.Client/State/StoreSubscriberComponent.cs (existing pattern)
private readonly CompositeDisposable _subscriptions = new();

public virtual void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _subscriptions.Dispose();
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Direct state mutation in handlers | Action dispatch pattern | Phase 4 | Testable, predictable state changes |
| Global event handling | Per-topic + global events | Phase 4 | Better performance, scoped updates |
| Manual subscription tracking | CompositeDisposable | Phase 1 | Cleaner disposal, no leaks |

## Open Questions

### 1. Dispatcher Async Support

**What we know:** Current `Dispatcher.Dispatch()` is synchronous. SignalR handlers are async.

**What's unclear:** Should dispatch be async? Or is fire-and-forget acceptable?

**Recommendation:** Keep dispatch synchronous. SignalR handlers can `await` any necessary async operations (like `InvokeAsync`) before dispatching. Reducers are always synchronous (pure functions).

### 2. StreamResumeService Integration

**What we know:** `StreamResumeService.TryResumeStreamAsync()` currently interacts with `ChatStateManager` directly.

**What's unclear:** Should this service also dispatch actions, or remain as-is since it handles streaming loop?

**Recommendation:** `StreamResumeService` should dispatch `StreamStarted`, `StreamChunk`, `StreamCompleted` actions as it processes the stream. This integrates it with the store pattern while keeping its async streaming loop.

### 3. Error Event Handling

**What we know:** CONTEXT.md says "Failed events: silent drop with logging, no retry or user notification".

**What's unclear:** Where should logging happen - in HubEventDispatcher or in SignalREventSubscriber?

**Recommendation:** Catch exceptions in `SignalREventSubscriber` handler callbacks, log, and swallow. HubEventDispatcher can assume valid input.

## Existing Actions to Use

Actions already defined in Phase 2 that HubEventDispatcher should dispatch:

### Topics
- `AddTopic(StoredTopic Topic)` - for TopicChangeType.Created
- `UpdateTopic(StoredTopic Topic)` - for TopicChangeType.Updated
- `RemoveTopic(string TopicId)` - for TopicChangeType.Deleted

### Streaming
- `StreamStarted(string TopicId)` - for StreamChangeType.Started
- `StreamCompleted(string TopicId)` - for StreamChangeType.Completed
- `StreamCancelled(string TopicId)` - for StreamChangeType.Cancelled

### Connection
- `ConnectionReconnecting` - for HubConnection.Reconnecting event
- `ConnectionReconnected` - for HubConnection.Reconnected event
- `ConnectionClosed(string? Error)` - for HubConnection.Closed event

### Approval
- `ApprovalResolved(string ApprovalId, string? ToolCalls)` - for ApprovalResolvedNotification
- (Need new action for tool calls notification - see below)

### Messages
- `MessagesLoaded(string TopicId, IReadOnlyList<ChatMessageModel> Messages)` - for NewMessageNotification (after fetching)

## New Actions Needed

Based on gap analysis:

| Action | Purpose | Notification Source |
|--------|---------|---------------------|
| `ToolCallsReceived(string TopicId, string ToolCalls)` | Add tool calls to streaming message | ToolCallsNotification |
| `NewMessageReceived(string TopicId)` | Signal new message available | NewMessageNotification |

## Sources

### Primary (HIGH confidence)
- Codebase analysis of existing implementation
- `WebChat.Client/Services/ChatConnectionService.cs` - current SignalR setup
- `WebChat.Client/Services/SignalREventSubscriber.cs` - current event registration
- `WebChat.Client/Services/Handlers/ChatNotificationHandler.cs` - current event handling
- `WebChat.Client/State/` - all Phase 1-3 store infrastructure

### Secondary (MEDIUM confidence)
- Microsoft.AspNetCore.SignalR.Client API patterns (verified against existing code)
- CONTEXT.md user decisions

### Tertiary (LOW confidence)
- None

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - all components from existing codebase
- Architecture: HIGH - extends established Phase 1-3 patterns
- Pitfalls: HIGH - derived from codebase analysis and common SignalR issues

**Research date:** 2026-01-20
**Valid until:** 2026-02-20 (stable codebase, no external dependencies changing)
