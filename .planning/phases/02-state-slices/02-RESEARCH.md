# Phase 2: State Slices - Research

**Researched:** 2026-01-20
**Domain:** Feature-specific state slices for Blazor WebAssembly chat application
**Confidence:** HIGH

## Summary

This phase creates five feature-specific state slices (TopicsState, MessagesState, StreamingState, ConnectionState, ApprovalState) to replace the monolithic `ChatStateManager`. The existing `ChatStateManager` (272 lines) manages all state in mutable dictionaries with a single `OnStateChanged` event, causing global re-renders on any state change.

The Phase 1 infrastructure is complete and ready for use: `Store<TState>` with BehaviorSubject, `Dispatcher` with `RegisterHandler<TAction>`, `StoreSubscriberComponent` with three `Subscribe` overloads, and `Selector` with memoization. Each state slice will have its own store, actions (as record types implementing `IAction`), and reducer functions.

**Primary recommendation:** Create five separate stores with immutable record state types, fine-grained actions per the CONTEXT.md decisions, and clear ownership boundaries. Messages should be normalized by `TopicId` as `Dictionary<string, IReadOnlyList<ChatMessageModel>>`. Streaming should track per-message with `Dictionary<string, StreamingContent>`. Topics should store only `SelectedTopicId` with full topic derivation via selector.

## Existing State Analysis

### Current ChatStateManager Data Shapes

The existing `ChatStateManager` manages these data structures:

| Field | Type | Purpose | Migration Target |
|-------|------|---------|------------------|
| `_messagesByTopic` | `Dictionary<string, List<ChatMessageModel>>` | Message history per topic | MessagesState |
| `_streamingMessageByTopic` | `Dictionary<string, ChatMessageModel?>` | Active streaming message per topic | StreamingState |
| `_streamingTopics` | `HashSet<string>` | Topics currently streaming | StreamingState |
| `_resumingTopics` | `HashSet<string>` | Topics resuming after reconnect | StreamingState |
| `_lastSeenMessageCountByTopic` | `Dictionary<string, int>` | Unread tracking | TopicsState (derived) |
| `_topics` | `List<StoredTopic>` | Topic list | TopicsState |
| `_agents` | `List<AgentInfo>` | Agent list | TopicsState (agents rarely change) |
| `SelectedTopic` | `StoredTopic?` | Current selection | TopicsState |
| `SelectedAgentId` | `string?` | Current agent | TopicsState |
| `CurrentApprovalRequest` | `ToolApprovalRequestMessage?` | Pending approval | ApprovalState |

### Current Streaming Mechanisms

The `StreamingCoordinator` handles:

1. **Throttled rendering** - 50ms throttle via `ThrottledRenderAsync` with lock-based coordination
2. **Message accumulation** - `AccumulateChunk` builds up Content, Reasoning, and ToolCalls
3. **Multi-turn handling** - Detects `MessageId` changes to separate conversation turns
4. **Buffer rebuilding** - `RebuildFromBuffer` for reconnection scenarios
5. **Known content stripping** - `StripKnownContent` deduplicates on resume

Key streaming fields per topic:
- `Content` - Accumulated text content
- `Reasoning` - Accumulated reasoning/thinking
- `ToolCalls` - Accumulated tool call descriptions
- `IsComplete` flag for turn completion
- `SequenceNumber` for ordering

### Current SignalR Connection Patterns

The `ChatConnectionService` exposes:
- `IsConnected` - `HubConnection?.State == HubConnectionState.Connected`
- `IsReconnecting` - `HubConnection?.State == HubConnectionState.Reconnecting`
- Events: `OnStateChanged`, `OnReconnected`, `OnReconnecting`

The retry policy uses aggressive delays: 0s, 1s, 2s, 5s, then 10s max.

### Current Approval Flow

1. `ChatStreamMessage.ApprovalRequest` arrives during streaming
2. `StateManager.SetApprovalRequest(request)` stores it
3. UI shows modal with tool name and arguments
4. User response via `ApprovalService.RespondToApprovalAsync(approvalId, result)`
5. `ApprovalResolvedNotification` clears the request via `SetApprovalRequest(null)`

The `ToolApprovalRequestMessage` contains:
- `ApprovalId` - Unique identifier
- `Requests` - List of `ToolApprovalRequest(ToolName, Arguments)`

Result options: `Rejected`, `Approved`, `ApprovedAndRemember`, `AutoApproved`

## Existing DTOs and Types

### Domain DTOs (Reusable)

| Type | Location | Fields |
|------|----------|--------|
| `TopicMetadata` | Domain/DTOs/WebChat | TopicId, ChatId, ThreadId, AgentId, Name, CreatedAt, LastMessageAt, LastReadMessageCount |
| `AgentInfo` | Domain/DTOs/WebChat | Id, Name, Description |
| `ChatStreamMessage` | Domain/DTOs/WebChat | Content, Reasoning, ToolCalls, IsComplete, Error, MessageId, SequenceNumber, ApprovalRequest |
| `ChatHistoryMessage` | Domain/DTOs/WebChat | Role, Content |
| `StreamState` | Domain/DTOs/WebChat | IsProcessing, BufferedMessages, CurrentMessageId, CurrentPrompt |
| `ToolApprovalRequestMessage` | Domain/DTOs/WebChat | ApprovalId, Requests |
| `ToolApprovalRequest` | Domain/DTOs | ToolName, Arguments |
| `ToolApprovalResult` | Domain/DTOs | Enum: Rejected, Approved, ApprovedAndRemember, AutoApproved |

### Client Models (May Need Conversion to Records)

| Type | Location | Fields | Notes |
|------|----------|--------|-------|
| `ChatMessageModel` | WebChat.Client/Models | Role, Content, Reasoning, ToolCalls, IsError, HasContent | Already a record |
| `StoredTopic` | WebChat.Client/Models | TopicId, ChatId, ThreadId, AgentId, Name, CreatedAt, LastMessageAt, LastReadMessageCount | Mutable class, needs conversion or wrapper |

### Hub Notifications (For Integration)

| Notification | Fields | Triggers |
|--------------|--------|----------|
| `TopicChangedNotification` | ChangeType, TopicId, Topic? | Topic CRUD |
| `StreamChangedNotification` | ChangeType, TopicId | Stream start/stop/cancel |
| `NewMessageNotification` | TopicId | New message available |
| `ApprovalResolvedNotification` | TopicId, ApprovalId, ToolCalls? | Approval complete |
| `ToolCallsNotification` | TopicId, ToolCalls | Tool execution info |

## Phase 1 Infrastructure (Available)

### Store<TState>

```csharp
public sealed class Store<TState> : IDisposable where TState : class
{
    public TState State { get; }
    public IObservable<TState> StateObservable { get; }
    public void Dispatch<TAction>(TAction action, Func<TState, TAction, TState> reducer);
    public void Dispose();
}
```

Uses `BehaviorSubject<TState>` - replays current value to late subscribers.

### Dispatcher

```csharp
public sealed class Dispatcher : IDispatcher
{
    public void RegisterHandler<TAction>(Action<TAction> handler) where TAction : IAction;
    public void Dispatch<TAction>(TAction action) where TAction : IAction;
}
```

Components inject `IDispatcher` (dispatch-only), stores inject concrete `Dispatcher` for registration.

### StoreSubscriberComponent

```csharp
public abstract class StoreSubscriberComponent : ComponentBase, IDisposable
{
    protected void Subscribe<T>(IObservable<T> observable, Action<T> onNext);
    protected void Subscribe<TState, TSelected>(
        IObservable<TState> stateObservable,
        Func<TState, TSelected> selector,
        Action<TSelected> onNext);
    protected void Subscribe<TState, TSelected>(
        IObservable<TState> stateObservable,
        Func<TState, TSelected> selector,
        IEqualityComparer<TSelected> comparer,
        Action<TSelected> onNext);
}
```

All subscriptions auto-disposed on component disposal.

### Selector

```csharp
public sealed class Selector<TState, TResult>
{
    public TResult Select(TState state);
    public void Invalidate();
}

public static class Selector
{
    public static Selector<TState, TResult> Create<TState, TResult>(Func<TState, TResult> projector);
    public static Selector<TState, TFinal> Compose<TState, TIntermediate, TFinal>(...);
}
```

Reference equality check for memoization - records create new instances on `with` mutations.

## Recommended State Slice Designs

### SLICE-01: TopicsState

**Purpose:** Topic list, selection, and agent information.

```csharp
public sealed record TopicsState
{
    public IReadOnlyList<StoredTopic> Topics { get; init; } = [];
    public string? SelectedTopicId { get; init; }
    public IReadOnlyList<AgentInfo> Agents { get; init; } = [];
    public string? SelectedAgentId { get; init; }
    public bool IsLoading { get; init; }
    public string? Error { get; init; }
}
```

**Actions:**
```csharp
public record LoadTopics : IAction;
public record TopicsLoaded(IReadOnlyList<StoredTopic> Topics) : IAction;
public record SelectTopic(string? TopicId) : IAction;
public record AddTopic(StoredTopic Topic) : IAction;
public record UpdateTopic(TopicMetadata Metadata) : IAction;
public record RemoveTopic(string TopicId) : IAction;
public record SetAgents(IReadOnlyList<AgentInfo> Agents) : IAction;
public record SelectAgent(string AgentId) : IAction;
public record TopicsError(string Message) : IAction;
```

**Key selectors:**
- `SelectedTopic` - derives full `StoredTopic` from `SelectedTopicId`
- `TopicsForAgent` - filters topics by `SelectedAgentId`
- `UnreadCounts` - computes unread per topic (needs MessagesState)

**Notes:**
- `StoredTopic` is currently a mutable class; can be wrapped or kept as-is since it's reference-compared
- `SelectTopic` stores ID only per CONTEXT.md decision
- Agent state rarely changes, grouping with topics is acceptable

### SLICE-02: MessagesState

**Purpose:** Message history per topic.

```csharp
public sealed record MessagesState
{
    public IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> MessagesByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ChatMessageModel>>();
    public IReadOnlySet<string> LoadedTopics { get; init; } = new HashSet<string>();
}
```

**Actions:**
```csharp
public record LoadMessages(string TopicId) : IAction;
public record MessagesLoaded(string TopicId, IReadOnlyList<ChatMessageModel> Messages) : IAction;
public record AddMessage(string TopicId, ChatMessageModel Message) : IAction;
public record UpdateMessage(string TopicId, int Index, ChatMessageModel Message) : IAction;
public record RemoveMessage(string TopicId, int Index) : IAction;
public record ClearMessages(string TopicId) : IAction;
```

**Key selectors:**
- `MessagesForTopic(topicId)` - returns messages for specific topic
- `HasMessagesForTopic(topicId)` - checks if loaded
- `CurrentMessages` - combines with TopicsState.SelectedTopicId

**Notes:**
- Dictionary keyed by TopicId enables O(1) topic switching
- Fine-grained actions per CONTEXT.md decision
- `ChatMessageModel` is already a record type

### SLICE-03: StreamingState

**Purpose:** Active streaming with throttled updates.

```csharp
public sealed record StreamingContent
{
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public string? CurrentMessageId { get; init; }
    public bool IsError { get; init; }
}

public sealed record StreamingState
{
    public IReadOnlyDictionary<string, StreamingContent> StreamingByTopic { get; init; }
        = new Dictionary<string, StreamingContent>();
    public IReadOnlySet<string> StreamingTopics { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ResumingTopics { get; init; } = new HashSet<string>();
}
```

**Actions:**
```csharp
public record StreamStarted(string TopicId) : IAction;
public record StreamChunk(string TopicId, string? Content, string? Reasoning, string? ToolCalls, string? MessageId) : IAction;
public record StreamCompleted(string TopicId, ChatMessageModel? FinalMessage) : IAction;
public record StreamCancelled(string TopicId) : IAction;
public record StreamError(string TopicId, string Error) : IAction;
public record StartResuming(string TopicId) : IAction;
public record StopResuming(string TopicId) : IAction;
```

**Key selectors:**
- `IsTopicStreaming(topicId)` - checks streaming status
- `StreamingContentForTopic(topicId)` - gets current streaming content
- `CurrentStreamingMessage` - combines with TopicsState.SelectedTopicId

**Notes:**
- Per-message tracking via Dictionary enables potential concurrent streams
- Separate actions for each streaming event per CONTEXT.md
- `StreamCompleted` can dispatch `AddMessage` to MessagesState (component coordination)
- Throttling handled at UI subscription level, not in store

### SLICE-04: ConnectionState

**Purpose:** SignalR connection status.

```csharp
public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

public sealed record ConnectionState
{
    public ConnectionStatus Status { get; init; } = ConnectionStatus.Disconnected;
    public DateTime? LastConnected { get; init; }
    public int ReconnectAttempts { get; init; }
    public string? Error { get; init; }
}
```

**Actions:**
```csharp
public record ConnectionStatusChanged(ConnectionStatus Status) : IAction;
public record ConnectionConnected : IAction;
public record ConnectionReconnecting : IAction;
public record ConnectionReconnected : IAction;
public record ConnectionClosed(string? Error) : IAction;
public record ConnectionError(string Error) : IAction;
```

**Key selectors:**
- `IsConnected` - `Status == ConnectionStatus.Connected`
- `IsReconnecting` - `Status == ConnectionStatus.Reconnecting`
- `ShouldShowBanner` - derives display logic

**Notes:**
- Included `LastConnected` and `ReconnectAttempts` per Claude's discretion allowance
- Error auto-clears on successful connection per CONTEXT.md
- Can be used for global connection banner

### SLICE-05: ApprovalState

**Purpose:** Tool approval modal.

```csharp
public sealed record ApprovalState
{
    public ToolApprovalRequestMessage? CurrentRequest { get; init; }
    public string? TopicId { get; init; }
    public bool IsResponding { get; init; }
}
```

**Actions:**
```csharp
public record ShowApproval(string TopicId, ToolApprovalRequestMessage Request) : IAction;
public record ApprovalResponding : IAction;
public record ApprovalResolved(string ApprovalId, string? ToolCalls) : IAction;
public record ClearApproval : IAction;
```

**Key selectors:**
- `HasPendingApproval` - `CurrentRequest != null`
- `ApprovalForCurrentTopic` - combines with TopicsState.SelectedTopicId

**Notes:**
- Simple state - request present or not
- `TopicId` tracks which topic the approval belongs to
- `IsResponding` for UI feedback during async response
- Approval triggers execution per CONTEXT.md (component dispatches response, which triggers stream continuation)

## Architecture Patterns

### Recommended Project Structure

```
WebChat.Client/
├── State/
│   ├── Store.cs                    # Phase 1 (exists)
│   ├── IAction.cs                  # Phase 1 (exists)
│   ├── IDispatcher.cs              # Phase 1 (exists)
│   ├── Dispatcher.cs               # Phase 1 (exists)
│   ├── StoreSubscriberComponent.cs # Phase 1 (exists)
│   ├── Selector.cs                 # Phase 1 (exists)
│   ├── Topics/
│   │   ├── TopicsState.cs          # State record
│   │   ├── TopicsActions.cs        # Action records
│   │   ├── TopicsReducers.cs       # Pure reducer functions
│   │   ├── TopicsSelectors.cs      # Memoized selectors
│   │   └── TopicsStore.cs          # Store wrapper with registration
│   ├── Messages/
│   │   ├── MessagesState.cs
│   │   ├── MessagesActions.cs
│   │   ├── MessagesReducers.cs
│   │   ├── MessagesSelectors.cs
│   │   └── MessagesStore.cs
│   ├── Streaming/
│   │   ├── StreamingState.cs
│   │   ├── StreamingActions.cs
│   │   ├── StreamingReducers.cs
│   │   ├── StreamingSelectors.cs
│   │   └── StreamingStore.cs
│   ├── Connection/
│   │   ├── ConnectionState.cs
│   │   ├── ConnectionActions.cs
│   │   ├── ConnectionReducers.cs
│   │   └── ConnectionStore.cs
│   └── Approval/
│       ├── ApprovalState.cs
│       ├── ApprovalActions.cs
│       ├── ApprovalReducers.cs
│       └── ApprovalStore.cs
```

### Store Wrapper Pattern

Each feature store wraps `Store<TState>` and registers handlers:

```csharp
public sealed class TopicsStore : IDisposable
{
    private readonly Store<TopicsState> _store;

    public TopicsStore(Dispatcher dispatcher)
    {
        _store = new Store<TopicsState>(TopicsState.Initial);

        dispatcher.RegisterHandler<LoadTopics>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));
        dispatcher.RegisterHandler<TopicsLoaded>(action =>
            _store.Dispatch(action, TopicsReducers.Reduce));
        // ... register all action handlers
    }

    public TopicsState State => _store.State;
    public IObservable<TopicsState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();
}
```

### Reducer Pattern

Pure static functions for each action:

```csharp
public static class TopicsReducers
{
    public static TopicsState Reduce(TopicsState state, IAction action) => action switch
    {
        TopicsLoaded a => state with
        {
            Topics = a.Topics,
            IsLoading = false,
            Error = null  // Auto-clear on success
        },
        SelectTopic a => state with { SelectedTopicId = a.TopicId },
        AddTopic a => state with
        {
            Topics = state.Topics.Append(a.Topic).ToList()
        },
        TopicsError a => state with { Error = a.Message, IsLoading = false },
        _ => state
    };
}
```

### Cross-Slice Coordination Pattern

Per CONTEXT.md, components coordinate cross-slice operations:

```csharp
// In component:
private async Task HandleSelectTopic(string topicId)
{
    // 1. Update topics state
    _dispatcher.Dispatch(new SelectTopic(topicId));

    // 2. Load messages if needed
    if (!_messagesStore.State.LoadedTopics.Contains(topicId))
    {
        _dispatcher.Dispatch(new LoadMessages(topicId));
        var topic = _topicsStore.State.Topics.First(t => t.TopicId == topicId);
        var history = await _topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
        _dispatcher.Dispatch(new MessagesLoaded(topicId, ConvertHistory(history)));
    }
}
```

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Dictionary immutability | Custom immutable dictionary | `new Dictionary<K,V>(existing) { [key] = value }` | Simple, clear mutation pattern |
| List immutability | Custom immutable list | `.ToList()` or `ImmutableList<T>` | Built-in, well-tested |
| Set immutability | Custom immutable set | `new HashSet<T>(existing) { item }` | Simple pattern |
| Stream throttling | Custom throttle in reducer | Rx `Throttle()` at subscription | Separates concerns |
| Cross-store dispatch | Store-to-store references | Component coordination | Keeps stores independent |

## Common Pitfalls

### Pitfall 1: Mutating Existing Collections in Reducers

**What goes wrong:** Reducer modifies existing dictionary/list instead of creating new one.

**Why it happens:** Habit from mutable programming, or performance optimization attempt.

**How to avoid:** Always create new collections in reducers:
```csharp
// BAD
state.MessagesByTopic[topicId] = messages; // Mutates existing dict
return state;

// GOOD
return state with
{
    MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
    {
        [topicId] = messages
    }
};
```

**Warning signs:** Multiple components seeing same state object after different actions.

### Pitfall 2: Forgetting to Register Action Handlers

**What goes wrong:** Action dispatched but nothing happens.

**Why it happens:** Added new action type but forgot to register handler in store constructor.

**How to avoid:**
- Keep action types and handler registrations in same file
- Add unit test for each action type
- Check dispatcher no-op behavior (currently silent fail)

**Warning signs:** Action dispatch has no effect, no errors thrown.

### Pitfall 3: Circular Dependencies Between Stores

**What goes wrong:** Store A needs Store B which needs Store A.

**Why it happens:** Trying to coordinate cross-slice state in stores instead of components.

**How to avoid:** Per CONTEXT.md, use component coordination:
- Stores never reference each other
- Components inject multiple stores
- Components dispatch sequences of actions

**Warning signs:** DI circular dependency error, complex store interdependencies.

### Pitfall 4: Expensive Selector Recomputation

**What goes wrong:** Selector runs on every state change, not just relevant changes.

**Why it happens:** Subscribing to entire state instead of using `DistinctUntilChanged`.

**How to avoid:**
```csharp
// BAD - recomputes on any state change
Subscribe(store.StateObservable, state => ExpensiveComputation(state.Topics));

// GOOD - only recomputes when Topics changes
Subscribe(
    store.StateObservable,
    state => state.Topics,
    topics => ExpensiveComputation(topics)
);
```

**Warning signs:** Lag during frequent state updates, excessive CPU usage.

### Pitfall 5: Storing Full Topic in Selection

**What goes wrong:** `SelectedTopic: StoredTopic` gets stale when topic updates.

**Why it happens:** Storing object reference instead of ID.

**How to avoid:** Per CONTEXT.md decision, store only ID:
```csharp
// State stores ID
public string? SelectedTopicId { get; init; }

// Derive full topic via selector
var selectedTopic = Selector.Create<TopicsState, StoredTopic?>(
    state => state.Topics.FirstOrDefault(t => t.TopicId == state.SelectedTopicId)
);
```

**Warning signs:** Selected topic shows old data after update.

## Code Examples

### Store Registration in DI

```csharp
// Program.cs
builder.Services.AddScoped<Dispatcher>();
builder.Services.AddScoped<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
builder.Services.AddScoped<TopicsStore>();
builder.Services.AddScoped<MessagesStore>();
builder.Services.AddScoped<StreamingStore>();
builder.Services.AddScoped<ConnectionStore>();
builder.Services.AddScoped<ApprovalStore>();
```

### Component Using Multiple Stores

```csharp
@inherits StoreSubscriberComponent
@inject TopicsStore TopicsStore
@inject MessagesStore MessagesStore
@inject IDispatcher Dispatcher

@code {
    private IReadOnlyList<StoredTopic> _topics = [];
    private IReadOnlyList<ChatMessageModel> _messages = [];
    private string? _selectedTopicId;

    protected override void OnInitialized()
    {
        Subscribe(TopicsStore.StateObservable,
            state => state.Topics,
            topics => _topics = topics);

        Subscribe(TopicsStore.StateObservable,
            state => state.SelectedTopicId,
            id => _selectedTopicId = id);

        // Derived subscription - messages for selected topic
        Subscribe(MessagesStore.StateObservable,
            state => _selectedTopicId != null
                ? state.MessagesByTopic.GetValueOrDefault(_selectedTopicId, [])
                : [],
            messages => _messages = messages);
    }
}
```

### Reducer with Error Auto-Clear

```csharp
public static MessagesState Reduce(MessagesState state, IAction action) => action switch
{
    MessagesLoaded a => state with
    {
        MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
        {
            [a.TopicId] = a.Messages
        },
        LoadedTopics = new HashSet<string>(state.LoadedTopics) { a.TopicId },
        // Error auto-clears on success per CONTEXT.md
    },
    AddMessage a => state with
    {
        MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>(state.MessagesByTopic)
        {
            [a.TopicId] = state.MessagesByTopic.GetValueOrDefault(a.TopicId, [])
                .Append(a.Message).ToList()
        }
    },
    _ => state
};
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Single ChatStateManager | Feature-specific stores | This refactoring | Isolated state, selective updates |
| Mutable dictionaries | Immutable records with `with` | This refactoring | Predictable state changes |
| Global OnStateChanged | Per-store IObservable subscriptions | This refactoring | Granular re-renders |
| StoredTopic reference in selection | ID-only selection | This refactoring | Always-fresh data via selector |

## Open Questions

### 1. StreamCompleted Message Transfer

**Decision needed:** Should `StreamCompleted` automatically dispatch `AddMessage` to MessagesStore?

**Options:**
- A) Reducer dispatches (coupling stores)
- B) Component dispatches sequence (per CONTEXT.md recommendation)
- C) Effect/middleware handles (future pattern)

**Recommendation:** Option B - component dispatches `StreamCompleted`, observes completion, then dispatches `AddMessage`. Keeps stores independent.

### 2. Loading State Location

**Decision needed:** Per-slice `IsLoading` vs centralized `LoadingState`?

**Options:**
- A) Per-slice: `TopicsState.IsLoading`, `MessagesState.IsLoading`
- B) Centralized: `LoadingState { TopicsLoading, MessagesLoading, ... }`

**Recommendation:** Option A - per-slice. Simpler, each slice owns its loading state, no cross-slice coordination needed.

### 3. Error Representation

**Decision needed:** String message vs structured error type?

**Options:**
- A) `string? Error` - simple, sufficient for display
- B) `ErrorInfo { Code, Message, Details }` - richer, enables categorization

**Recommendation:** Option A for Phase 2. String is sufficient for current needs. Can upgrade to structured errors if categorization becomes necessary.

## Sources

### Primary (HIGH confidence)
- `WebChat.Client/Services/State/ChatStateManager.cs` - Current implementation (272 lines analyzed)
- `WebChat.Client/Services/Streaming/StreamingCoordinator.cs` - Streaming logic (417 lines analyzed)
- `WebChat.Client/Services/ChatConnectionService.cs` - Connection patterns (91 lines analyzed)
- `WebChat.Client/Services/ApprovalService.cs` - Approval flow (30 lines analyzed)
- `WebChat.Client/State/*.cs` - Phase 1 infrastructure (Store, Dispatcher, StoreSubscriberComponent, Selector)
- `Domain/DTOs/WebChat/*.cs` - All DTOs analyzed
- `.planning/phases/02-state-slices/02-CONTEXT.md` - User decisions constraining design

### Secondary (MEDIUM confidence)
- `Tests/Unit/WebChat/Client/ChatStateManagerTests.cs` - 761 lines of behavior specification
- `Tests/Unit/WebChat/Client/StreamingCoordinatorTests.cs` - 583 lines of streaming behavior
- `.planning/phases/01-state-foundation/01-RESEARCH.md` - Phase 1 patterns and decisions

### Codebase References
- `WebChat.Client/Models/ChatMessageModel.cs` - Already a record, can use directly
- `WebChat.Client/Models/StoredTopic.cs` - Mutable class, needs consideration
- `WebChat.Client/Services/Handlers/ChatNotificationHandler.cs` - Hub event handling patterns
- `WebChat.Client/Services/SignalREventSubscriber.cs` - Event subscription patterns

## Metadata

**Confidence breakdown:**
- State shapes: HIGH - Direct analysis of existing code
- Action granularity: HIGH - Specified in CONTEXT.md
- Cross-slice patterns: HIGH - Specified in CONTEXT.md
- Streaming details: HIGH - Full StreamingCoordinator analysis
- Connection patterns: HIGH - Full ChatConnectionService analysis

**Research date:** 2026-01-20
**Valid until:** 2026-02-20 (codebase-specific research, 30-day validity)

---

*Phase: 02-state-slices*
*Research complete: 2026-01-20*
