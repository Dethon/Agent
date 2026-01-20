# Phase 5: Component Architecture - Research

**Researched:** 2026-01-20
**Domain:** Blazor component decomposition, store-based state subscription, effect pattern for complex operations
**Confidence:** HIGH

## Summary

Phase 5 refactors ChatContainer.razor (~305 lines) into focused, thin render components that dispatch actions and consume store state. The existing store infrastructure (Phases 1-3) and SignalR integration (Phase 4) provide the foundation. Components will subscribe directly to store slices via `StoreSubscriberComponent`, dispatch actions via `IDispatcher`, and delegate complex operations to Effect classes.

The key architectural shift removes all `ChatStateManager` usage from components. State reads come from store subscriptions; state writes happen through action dispatch. Complex multi-step operations (send message, topic selection) trigger Effect classes that orchestrate async workflows.

**Primary recommendation:** Extract components in leaf-to-root order (ConnectionStatus, ChatInput, ApprovalModal, MessageArea, TopicList), each inheriting `StoreSubscriberComponent` and injecting only `IDispatcher` for mutations. Create Effect classes for operations requiring async coordination.

## Standard Stack

The architecture uses patterns established in Phases 1-4:

### Core (Already Established)
| Component | Purpose | Location |
|-----------|---------|----------|
| `Dispatcher` | Routes actions to store handlers | `State/Dispatcher.cs` |
| `IDispatcher` | Interface for component injection | `State/IDispatcher.cs` |
| `Store<TState>` | BehaviorSubject-based reactive store | `State/Store.cs` |
| `IAction` | Marker interface for all actions | `State/IAction.cs` |
| `StoreSubscriberComponent` | Base component with subscription management | `State/StoreSubscriberComponent.cs` |
| `RenderCoordinator` | Throttled observables for streaming | `State/RenderCoordinator.cs` |

### Stores (Already Established)
| Store | State | Location |
|-------|-------|----------|
| `TopicsStore` | Topics, agents, selection | `State/Topics/TopicsStore.cs` |
| `MessagesStore` | Per-topic messages | `State/Messages/MessagesStore.cs` |
| `StreamingStore` | Per-topic streaming content | `State/Streaming/StreamingStore.cs` |
| `ConnectionStore` | Connection status | `State/Connection/ConnectionStore.cs` |
| `ApprovalStore` | Tool approval state | `State/Approval/ApprovalStore.cs` |

### Effects (Pattern Established)
| Component | Purpose | Location |
|-----------|---------|----------|
| `ReconnectionEffect` | Handles reconnection side effects | `State/Hub/ReconnectionEffect.cs` |

### Components to Extract
| Component | UI Region | Parent |
|-----------|-----------|--------|
| `ConnectionStatus` | Connection indicator | ChatContainer |
| `ChatInput` | Message input area | ChatContainer |
| `ApprovalModal` | Tool approval dialog | MessageArea |
| `MessageArea` | Messages + streaming + empty state | ChatContainer |
| `TopicList` | Sidebar with topics | ChatContainer |
| `TopicItem` | Individual topic row | TopicList |

## Architecture Patterns

### Pattern 1: StoreSubscriberComponent Base Class

**What:** All stateful components inherit from `StoreSubscriberComponent` to manage reactive subscriptions with automatic disposal.

**When to use:** Any component that reads state from stores.

**Example:**
```csharp
// Source: WebChat.Client/State/StoreSubscriberComponent.cs (existing)
@inherits StoreSubscriberComponent
@inject IDispatcher Dispatcher
@inject ConnectionStore ConnectionStore

@code {
    private bool _isConnected;
    private bool _isReconnecting;

    protected override void OnInitialized()
    {
        Subscribe(ConnectionStore.StateObservable,
            state => state.Status,
            status => {
                _isConnected = status == ConnectionStatus.Connected;
                _isReconnecting = status == ConnectionStatus.Reconnecting;
            });
    }
}
```

**Key methods:**
- `Subscribe<T>(observable, onNext)` - Basic subscription with auto re-render
- `Subscribe<TState, TSelected>(observable, selector, onNext)` - Selector with DistinctUntilChanged
- `SubscribeWithInvoke<T>(observable, onNext)` - For pre-throttled observables (RenderCoordinator)
- `ClearSubscriptions()` - Re-subscribe on parameter changes

### Pattern 2: Action-Only Dispatch

**What:** Components inject `IDispatcher` only, never stores directly for mutations. All state changes happen through action dispatch.

**When to use:** Every user interaction that modifies state.

**Incorrect:**
```csharp
// BAD: Directly accessing store methods
_topicsStore.State.Topics.Add(newTopic);
```

**Correct:**
```csharp
// GOOD: Dispatch action
Dispatcher.Dispatch(new AddTopic(newTopic));
```

**Component injection pattern:**
```csharp
// Components inject IDispatcher (write) and stores (read)
@inject IDispatcher Dispatcher       // For dispatching actions
@inject TopicsStore TopicsStore      // For subscribing to state
@inject ConnectionStore ConnectionStore
```

### Pattern 3: Effect Classes for Complex Operations

**What:** Multi-step async operations are handled by Effect classes that subscribe to stores and coordinate side effects.

**When to use:** Operations requiring multiple async calls, service interactions, or state transitions.

**Already implemented:** `ReconnectionEffect` subscribes to `ConnectionStore`, detects transitions, triggers session restart and stream resumption.

**New effects needed:**
- `SendMessageEffect` - Coordinates topic creation, session start, streaming
- `TopicSelectionEffect` - Handles topic switch with session management

**Example:**
```csharp
// Source: Pattern from WebChat.Client/State/Hub/ReconnectionEffect.cs
public sealed class SendMessageEffect : IDisposable
{
    private readonly IDisposable _subscription;

    public SendMessageEffect(
        Dispatcher dispatcher,  // Effects register action handlers
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        IStreamingCoordinator streamingCoordinator)
    {
        // Register to handle SendMessage action
        dispatcher.RegisterHandler<SendMessage>(async action =>
        {
            var state = topicsStore.State;
            // 1. Create topic if needed
            // 2. Start session
            // 3. Dispatch StreamStarted
            // 4. Start streaming coordination
        });
    }

    public void Dispose() => _subscription.Dispose();
}
```

### Pattern 4: Selector Functions for Derived State

**What:** Complex state derivations (unread counts, filtered topics) use selector functions for memoization.

**When to use:** When subscribing to computed/derived state.

**Example:**
```csharp
// Selector for topics filtered by agent
public static class TopicsSelectors
{
    public static Func<TopicsState, IReadOnlyList<StoredTopic>> SelectTopicsForAgent(string agentId) =>
        state => state.Topics.Where(t => t.AgentId == agentId)
            .OrderByDescending(t => t.LastMessageAt ?? t.CreatedAt)
            .ToList();
}

// Usage in component
Subscribe(TopicsStore.StateObservable,
    TopicsSelectors.SelectTopicsForAgent(SelectedAgentId),
    topics => _filteredTopics = topics);
```

### Pattern 5: Combined View Model for Multiple Stores

**What:** When a component needs data from multiple stores, create a combined selector that produces a view model.

**When to use:** Components needing coordinated state from 2+ stores.

**Example:**
```csharp
// View model combining topics and streaming state
public record TopicListViewModel(
    IReadOnlyList<StoredTopic> Topics,
    string? SelectedTopicId,
    IReadOnlySet<string> StreamingTopicIds,
    IReadOnlyDictionary<string, int> UnreadCounts);

// Combined observable
var combined = topicsStore.StateObservable
    .CombineLatest(streamingStore.StateObservable, (topics, streaming) =>
        new TopicListViewModel(
            topics.Topics,
            topics.SelectedTopicId,
            streaming.ActiveStreams.Keys.ToHashSet(),
            ComputeUnreadCounts(topics)));
```

### Anti-Patterns to Avoid

- **ChatStateManager usage:** Components must not inject or use `IChatStateManager` - use stores instead
- **Direct state mutation:** Never modify store state directly; always dispatch actions
- **Business logic in components:** Components render and dispatch; reducers and effects handle logic
- **Prop drilling:** Child components subscribe to stores directly rather than receiving props from parents
- **Large monolithic components:** Split when >100 lines or handling multiple concerns

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Subscription lifecycle | Manual subscribe/unsubscribe | `StoreSubscriberComponent` | CompositeDisposable handles cleanup |
| State derivation | Computed properties | Selector functions | Memoization via DistinctUntilChanged |
| Multi-store coordination | Manual combining | `CombineLatest` from Rx.NET | Reactive composition |
| Async operation coordination | Nested callbacks | Effect classes | Separation of concerns |
| Render throttling | Manual debounce | `RenderCoordinator` | 50ms Sample already configured |

**Key insight:** The store infrastructure handles complexity; components should be simple wiring of state to UI.

## Common Pitfalls

### Pitfall 1: Forgetting to Inherit StoreSubscriberComponent

**What goes wrong:** Memory leaks, events firing on disposed components.

**Why it happens:** Creating subscriptions without proper lifecycle management.

**How to avoid:** Always use `@inherits StoreSubscriberComponent` for stateful components.

**Warning signs:** "Disposed" errors in console, growing memory usage.

### Pitfall 2: Multiple Renders from Multiple Subscriptions

**What goes wrong:** Component re-renders multiple times per state change.

**Why it happens:** Each `Subscribe()` call triggers `StateHasChanged()` independently.

**How to avoid:**
- Use combined selectors for related state
- Use selector with `DistinctUntilChanged` to filter duplicates
- Batch related state into single subscription where possible

**Warning signs:** Excessive render counts, slow UI updates.

### Pitfall 3: Async Operations in Component Handlers

**What goes wrong:** Complex async flows become tangled in components.

**Why it happens:** Trying to coordinate multiple async calls in event handlers.

**How to avoid:** Dispatch a single action; let an Effect handle coordination.

```csharp
// BAD: Complex async in component
private async Task HandleSend(string message)
{
    var topic = await CreateTopicIfNeeded();
    await SessionService.StartSessionAsync(topic);
    Dispatcher.Dispatch(new StreamStarted(topic.TopicId));
    await StreamingCoordinator.StreamAsync(...);
}

// GOOD: Dispatch action, Effect handles coordination
private void HandleSend(string message)
{
    Dispatcher.Dispatch(new SendMessage(message));
}
```

**Warning signs:** Long async methods in components, hard-to-test handlers.

### Pitfall 4: Breaking Component Isolation

**What goes wrong:** Child component updates cause parent re-renders.

**Why it happens:** Passing state down as props instead of letting children subscribe.

**How to avoid:** Child components subscribe to store slices they need.

```csharp
// BAD: Parent passes props, causing cascade
<MessageList Messages="@StateManager.CurrentMessages" />

// GOOD: Child subscribes directly
// In MessageList.razor:
@inject MessagesStore MessagesStore
@code {
    [Parameter, EditorRequired] public string TopicId { get; set; }
    private IReadOnlyList<ChatMessageModel> _messages = [];

    protected override void OnInitialized() {
        Subscribe(MessagesStore.StateObservable,
            state => state.MessagesByTopic.GetValueOrDefault(TopicId, []),
            msgs => _messages = msgs);
    }
}
```

**Warning signs:** Entire component tree re-rendering on streaming updates.

### Pitfall 5: Missing Selector Equality Comparison

**What goes wrong:** Re-renders even when selected value hasn't changed.

**Why it happens:** C# records create new instances on `with` mutations.

**How to avoid:** Use reference equality or custom comparer.

```csharp
// Default: Reference equality (works for primitives, strings)
Subscribe(store.StateObservable, state => state.SelectedId, HandleSelection);

// For collections: Use sequence equality comparer
Subscribe(store.StateObservable,
    state => state.Topics,
    new CollectionEqualityComparer<StoredTopic>(),
    HandleTopics);
```

**Warning signs:** Infinite re-render loops, excessive CPU usage.

## Code Examples

### ConnectionStatus Component (Simplest Example)

```csharp
// Target: thin component subscribing to ConnectionStore
@inherits StoreSubscriberComponent
@inject ConnectionStore ConnectionStore

@code {
    private ConnectionStatus _status;

    protected override void OnInitialized()
    {
        Subscribe(ConnectionStore.StateObservable,
            state => state.Status,
            status => _status = status);
    }
}

@if (_status == ConnectionStatus.Reconnecting)
{
    <div class="connection-status reconnecting">Reconnecting...</div>
}
else if (_status == ConnectionStatus.Disconnected)
{
    <div class="connection-status disconnected">Disconnected</div>
}
```

### ChatInput Component (Dispatch Pattern)

```csharp
// Target: dispatch-only component
@inject IDispatcher Dispatcher
@inject IJSRuntime Js

@code {
    [Parameter] public string TopicId { get; set; } = "";
    [Parameter] public bool Disabled { get; set; }

    private string _inputText = "";
    private ElementReference _textareaRef;

    private async Task HandleSubmit()
    {
        if (string.IsNullOrWhiteSpace(_inputText) || Disabled) return;

        var message = _inputText;
        _inputText = "";
        await Js.InvokeVoidAsync("chatInput.reset", _textareaRef);

        // Single action dispatch - Effect handles complexity
        Dispatcher.Dispatch(new SendMessage(TopicId, message));
    }
}
```

### TopicList Component (Multiple Subscriptions)

```csharp
// Target: subscribes to multiple slices
@inherits StoreSubscriberComponent
@inject TopicsStore TopicsStore
@inject StreamingStore StreamingStore
@inject IDispatcher Dispatcher

@code {
    private IReadOnlyList<StoredTopic> _topics = [];
    private string? _selectedTopicId;
    private string? _selectedAgentId;
    private HashSet<string> _streamingTopics = new();

    protected override void OnInitialized()
    {
        Subscribe(TopicsStore.StateObservable, state => state.SelectedAgentId,
            agentId => {
                _selectedAgentId = agentId;
                UpdateFilteredTopics();
            });
        Subscribe(TopicsStore.StateObservable, state => state.Topics,
            _ => UpdateFilteredTopics());
        Subscribe(TopicsStore.StateObservable, state => state.SelectedTopicId,
            id => _selectedTopicId = id);
        Subscribe(StreamingStore.StateObservable,
            state => state.ActiveStreams.Keys.ToHashSet(),
            new SetEqualityComparer<string>(),
            ids => _streamingTopics = ids);
    }

    private void UpdateFilteredTopics() { ... }

    private void HandleTopicClick(string topicId)
    {
        Dispatcher.Dispatch(new SelectTopic(topicId));
    }
}
```

### SendMessageEffect (Complex Operation)

```csharp
// New Effect class for send message coordination
public sealed class SendMessageEffect : IDisposable
{
    public SendMessageEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        IChatSessionService sessionService,
        IStreamingCoordinator streamingCoordinator,
        ITopicService topicService,
        ILocalStorageService localStorage)
    {
        dispatcher.RegisterHandler<SendMessage>(action =>
        {
            _ = HandleSendMessageAsync(action, topicsStore.State);
        });
    }

    private async Task HandleSendMessageAsync(SendMessage action, TopicsState state)
    {
        StoredTopic topic;

        if (string.IsNullOrEmpty(action.TopicId))
        {
            // Create new topic
            topic = CreateTopic(action.Message, state.SelectedAgentId!);
            _dispatcher.Dispatch(new AddTopic(topic));
            _dispatcher.Dispatch(new SelectTopic(topic.TopicId));
            await _topicService.SaveTopicAsync(topic.ToMetadata(), isNew: true);
        }
        else
        {
            topic = state.Topics.First(t => t.TopicId == action.TopicId);
        }

        await _sessionService.StartSessionAsync(topic);

        _dispatcher.Dispatch(new AddMessage(topic.TopicId,
            new ChatMessageModel { Role = "user", Content = action.Message }));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _ = _streamingCoordinator.StreamResponseAsync(topic, action.Message);
    }
}
```

## New Actions Needed

| Action | Purpose | Triggered By |
|--------|---------|--------------|
| `SendMessage(string? TopicId, string Message)` | Initiate message send | ChatInput component |
| `SelectTopicAndLoad(string TopicId)` | Select topic with history load | TopicList component |
| `DeleteTopic(string TopicId)` | Delete topic with cleanup | TopicList component |
| `CancelStreaming(string TopicId)` | Cancel active stream | ChatInput component |
| `CreateNewTopic` | Deselect topic for new conversation | TopicList component |

## Extraction Order

Based on CONTEXT.md decisions, extract in leaf-to-root order:

1. **ConnectionStatus** (~20 lines)
   - Already exists and is simple
   - Migrate to store subscription

2. **ChatInput** (~70 lines)
   - Exists, needs dispatch pattern
   - Remove direct service calls

3. **ApprovalModal** (~114 lines)
   - Already exists
   - Move inside MessageArea
   - Subscribe to ApprovalStore

4. **MessageArea** (new, ~60 lines)
   - Extracts from ChatContainer
   - Contains MessageList + ApprovalModal + streaming

5. **TopicList** (~212 lines)
   - Already exists but large
   - Extract TopicItem (~40 lines each)
   - Migrate to store subscriptions

6. **ChatContainer** (reduced to ~80 lines)
   - Becomes composition root
   - Handles initial data loading only
   - Coordinates layout, no state management

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| ChatStateManager event subscription | Store observable subscription | Reactive, composable |
| Props passed through component tree | Child subscribes to store slice | No cascade re-renders |
| Business logic in components | Effects handle coordination | Testable, maintainable |
| Large monolithic components | Focused <100 line components | Readable, single-purpose |

## Open Questions

### 1. LocalStorage Persistence

**What we know:** Currently `ChatContainer` saves `selectedAgentId` to localStorage on agent change.

**What's unclear:** Should this be an Effect or inline in component?

**Recommendation:** Keep inline for now - it's a simple side effect. If more localStorage operations are needed, extract a `LocalStorageEffect`.

### 2. MessageList @ref Pattern

**What we know:** `ChatContainer` uses `@ref="_messageList"` to call `SetShouldAutoScroll()` and `CheckAndUpdateAutoScroll()`.

**What's unclear:** How to handle imperative child component calls in store pattern.

**Recommendation:** Move auto-scroll state into `MessagesStore` or keep as local component state. Auto-scroll is UI behavior, not application state.

### 3. InvokeRender Callback Pattern

**What we know:** `StreamResumeService.SetRenderCallback(InvokeRender)` triggers re-renders from streaming.

**What's unclear:** Is this needed with store subscriptions?

**Recommendation:** Components subscribed via `StoreSubscriberComponent` auto-re-render on state change. The callback pattern can be removed once streaming flows through stores.

## Sources

### Primary (HIGH confidence)
- Codebase analysis of existing implementation
- `WebChat.Client/State/StoreSubscriberComponent.cs` - existing subscription base
- `WebChat.Client/State/Hub/ReconnectionEffect.cs` - existing effect pattern
- `WebChat.Client/Components/Chat/` - existing component structure
- `WebChat.Client/Services/State/ChatStateManager.cs` - state to migrate away from
- `.planning/phases/05-component-architecture/05-CONTEXT.md` - user decisions

### Secondary (MEDIUM confidence)
- [Microsoft Blazor Rendering Documentation](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/rendering?view=aspnetcore-10.0)
- [Jon Hilton: When to Refactor Blazor Components](https://jonhilton.net/refactor-to-components/)
- [Telerik: Component Composition](https://www.telerik.com/blogs/component-composition-secret-scalable-maintainable-blazor-ui)

### Tertiary (LOW confidence)
- Web search results on Blazor state management patterns (verified against existing codebase patterns)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - all components exist in codebase
- Architecture patterns: HIGH - extends established Phase 1-4 patterns
- Pitfalls: HIGH - derived from Blazor rendering docs and codebase analysis
- Code examples: HIGH - based on existing patterns in codebase

**Research date:** 2026-01-20
**Valid until:** 2026-02-20 (stable internal architecture, no external dependencies)
