# Feature Landscape: Blazor State Management for Real-Time Chat

**Domain:** State management patterns for Blazor WebAssembly real-time chat application
**Researched:** 2026-01-19
**Confidence:** HIGH (verified via official Microsoft docs, established patterns)

## Table Stakes

Features required for maintainable state management. Missing = codebase becomes unmaintainable as it grows.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Single source of truth | Prevents inconsistent UI, eliminates "which state is current?" bugs | Medium | Central state container pattern |
| Unidirectional data flow | Makes state changes predictable and debuggable | Medium | Action -> Reducer -> State -> UI |
| Clear state ownership | Components know who owns state vs who consumes it | Low | Essential for maintainability |
| State change notification | Components must know when to re-render | Low | Event-based or subscription-based |
| Immutable state updates | Prevents accidental mutations, enables change detection | Medium | C# records make this natural |
| Separation of UI and state logic | Components focus on rendering, services manage state | Medium | Keeps components testable |
| Scoped lifetime for user state | Each user/circuit gets isolated state | Low | Scoped DI registration |
| Thread-safe state mutations | SignalR callbacks run on different threads | Medium | Critical for real-time apps |

### Table Stakes Details

#### 1. Single Source of Truth
**What:** All application state lives in a centralized store, not scattered across components.

**Why critical for chat app:**
- Multiple components need same data (message list, topic list, unread counts)
- SignalR events update state from outside component tree
- Stream resumption after reconnect needs consistent state

**Current codebase status:** Partially implemented via `ChatStateManager`, but mixed with business logic.

**Complexity:** Medium - requires defining clear state boundaries.

---

#### 2. Unidirectional Data Flow
**What:** State changes follow a predictable path: Action -> State Update -> UI Re-render.

**Why critical for chat app:**
- Bidirectional updates make debugging impossible ("where did this state change come from?")
- Real-time systems have many async state sources (SignalR, streams, reconnects)
- Current pain point in codebase: hard to trace state mutations

**Pattern:**
```
User Action -> Dispatch Action -> Reducer modifies state -> State notifies subscribers -> Components re-render
```

**Complexity:** Medium - requires discipline to avoid shortcuts.

---

#### 3. Clear State Ownership
**What:** Each piece of state has exactly one owner. Consumers read state, owners mutate it.

**Why critical for chat app:**
- Current problem: Multiple services can mutate same state
- Topics, messages, streaming state, approval state all need clear owners
- Prevents "who updated this?" debugging sessions

**Domain-specific state slices:**
| State Slice | Owner | Consumers |
|-------------|-------|-----------|
| Topics | TopicStateSlice | TopicList, ChatContainer |
| Messages | MessageStateSlice | MessageList, ChatContainer |
| Streaming | StreamingStateSlice | MessageList, StreamingCoordinator |
| Connection | ConnectionStateSlice | ConnectionStatus, all components |
| Agent | AgentStateSlice | AgentSelector, ChatContainer |
| Approval | ApprovalStateSlice | ApprovalModal |

**Complexity:** Low - primarily organizational.

---

#### 4. State Change Notification
**What:** When state changes, affected components are notified and re-render.

**Why critical for chat app:**
- Components outside the mutation call chain must update
- SignalR events change state from hub callbacks, not UI events
- Streaming updates happen at 50ms intervals

**Options:**
- Event-based (`OnStateChanged` event)
- Observable-based (IObservable pattern)
- Subscription-based (Fluxor-style state subscriptions)

**Current codebase:** Uses `OnStateChanged` event, requires manual `StateHasChanged()` calls.

**Complexity:** Low for events, Medium for observables.

---

#### 5. Immutable State Updates
**What:** State is never mutated in place; always create new state objects.

**Why critical for chat app:**
- Enables change detection (reference equality check)
- Prevents accidental side effects during streaming
- Makes undo/state history possible
- C# records provide `with` syntax for easy immutable updates

**Example:**
```csharp
// Bad: mutable
state.Messages.Add(newMessage);

// Good: immutable
state = state with {
    Messages = state.Messages.Append(newMessage).ToImmutableList()
};
```

**Complexity:** Medium - requires consistent discipline and immutable collections.

---

#### 6. Separation of UI and State Logic
**What:** Components render UI and dispatch actions. Services/reducers manage state transitions.

**Why critical for chat app:**
- Current `ChatContainer.razor` is 320 lines with mixed concerns
- Makes components untestable (can't unit test without SignalR)
- Business logic in components prevents reuse

**Target:**
- Components: ~50-100 lines, UI only
- State management: Separate services with pure functions where possible

**Complexity:** Medium - requires refactoring.

---

#### 7. Scoped Lifetime for User State
**What:** State container registered as Scoped in DI, not Singleton.

**Why critical for chat app:**
- In Blazor WASM, Scoped = Singleton (one user per app)
- In Blazor Server, Scoped = per circuit (important for multi-user)
- Prevents state leaking between users if server-side rendering added later

**Complexity:** Low - correct DI registration.

---

#### 8. Thread-Safe State Mutations
**What:** State updates are safe when called from different threads/contexts.

**Why critical for chat app:**
- SignalR hub callbacks run on SignalR thread
- Streaming uses background tasks for throttled rendering
- `InvokeAsync(StateHasChanged)` pattern already used but inconsistently

**Pattern:**
```csharp
// Always dispatch to UI thread for state mutations
await InvokeAsync(() => stateManager.UpdateState(...));
```

**Complexity:** Medium - must audit all state mutation points.

---

## Differentiators

Features that improve developer experience and maintainability. Not required, but make the codebase significantly better.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Selective re-rendering | Only affected components re-render on state change | Medium | Prevents wasted renders |
| State selectors | Components subscribe to specific state slices | Medium | Fine-grained updates |
| Effects/side-effect handlers | Async operations separated from reducers | Medium | Clean async handling |
| Action logging/debugging | See all state changes in dev tools | Low | Redux DevTools integration possible |
| State persistence hooks | Save/restore state across reconnects | Medium | Already partially implemented |
| Computed/derived state | Memoized calculations from base state | Low | Avoids redundant computation |
| Optimistic updates | Update UI immediately, reconcile with server | High | Better perceived performance |
| State validation | Validate state transitions | Medium | Catch bugs early |

### Differentiators Details

#### 1. Selective Re-Rendering
**What:** When state changes, only components that depend on changed data re-render.

**Value for chat app:**
- Current: `OnStateChanged` triggers `StateHasChanged()` for ALL subscribed components
- With many topics and messages, this causes unnecessary renders
- Streaming at 50ms updates is particularly expensive

**Implementation options:**
- Fluxor: Use `IState<T>` for specific state slices
- Custom: Components subscribe to specific state slice events
- Override `ShouldRender()` with reference equality checks

**Complexity:** Medium - requires state slice architecture.

---

#### 2. State Selectors
**What:** Functions that derive specific data from the state store.

**Value for chat app:**
```csharp
// Instead of:
var unreadCount = stateManager.GetAssistantMessageCount(topicId) - stateManager.GetLastReadCount(topicId);

// Use selector:
var unreadCount = stateManager.Select(s => s.UnreadCountFor(topicId));
```

**Benefits:**
- Memoization: recalculate only when inputs change
- Testable: pure functions
- Reusable: same selector across components

**Complexity:** Medium - requires selector infrastructure.

---

#### 3. Effects/Side-Effect Handlers
**What:** Separate service for async operations triggered by actions.

**Value for chat app:**
- Current: `ChatContainer` mixes UI events with async operations
- Effects handle: API calls, SignalR operations, stream management
- Keeps reducers pure (no async)

**Pattern:**
```
Action dispatched -> Reducer updates state (sync) -> Effect handles async work -> Dispatches result action
```

**Example for chat:**
```csharp
[EffectMethod]
public async Task HandleSendMessage(SendMessageAction action, IDispatcher dispatcher)
{
    dispatcher.Dispatch(new StartStreamingAction(action.TopicId));
    await foreach (var chunk in messagingService.SendMessageAsync(...))
    {
        dispatcher.Dispatch(new StreamChunkReceivedAction(chunk));
    }
    dispatcher.Dispatch(new StreamCompletedAction(action.TopicId));
}
```

**Complexity:** Medium - significant pattern shift.

---

#### 4. Action Logging/Debugging
**What:** Log or visualize all dispatched actions and resulting state changes.

**Value for chat app:**
- Debug complex state flows (reconnection, stream resumption)
- Understand "how did we get to this state?"
- Fluxor includes Redux DevTools integration

**Implementation:**
- Middleware that logs actions
- State diff visualization
- Time-travel debugging (replay actions)

**Complexity:** Low with Fluxor, Medium custom.

---

#### 5. State Persistence Hooks
**What:** Hooks for saving/restoring state at key moments.

**Value for chat app:**
- Already have stream resumption complexity
- Could save UI state (scroll position, expanded sections)
- LocalStorage for user preferences already implemented

**Pattern:**
```csharp
// On disconnect
persistenceService.SaveState(currentState);

// On reconnect
var savedState = persistenceService.LoadState();
dispatcher.Dispatch(new HydrateStateAction(savedState));
```

**Complexity:** Medium - need to decide what to persist.

---

#### 6. Computed/Derived State
**What:** Values calculated from base state, memoized to avoid recomputation.

**Value for chat app:**
- `UnreadCounts` computed from messages and last-read positions
- `CurrentMessages` derived from selected topic
- `IsCurrentTopicStreaming` derived from streaming topics set

**Current:** Computed properties recalculate on every access.

**Pattern:**
```csharp
// Memoized selector
public IReadOnlyDictionary<string, int> UnreadCounts =>
    _unreadCountsSelector.Select(
        state => state.MessagesByTopic,
        state => state.LastReadCounts,
        (msgs, reads) => ComputeUnreadCounts(msgs, reads)
    );
```

**Complexity:** Low - standard memoization pattern.

---

## Anti-Features

Patterns to deliberately avoid. Common in the wild but cause problems.

| Anti-Feature | Why Avoid | What to Do Instead |
|--------------|-----------|-------------------|
| Bidirectional binding for complex state | Unpredictable updates, impossible to debug | Unidirectional flow with explicit actions |
| Component-held shared state | State lost on component disposal, inconsistent | Centralized state service |
| Passing state via constructor parameters | Creates tight coupling, difficult to test | Inject state service, use state subscriptions |
| Direct SignalR -> Component updates | Bypasses state management, creates duplicates | SignalR -> Dispatch action -> State -> Component |
| Mutable state objects | Hidden mutations, breaks change detection | Immutable records with `with` expressions |
| Global re-renders on any state change | Performance killer for streaming apps | Selective subscriptions to state slices |
| Cascading parameters for app state | Creates implicit dependencies, hard to trace | Explicit DI-injected state services |
| State in local component fields | Lost on re-render, inconsistent | State service with proper lifetime |
| Calling StateHasChanged without InvokeAsync | Thread safety issues with SignalR | Always `InvokeAsync(StateHasChanged)` from callbacks |
| Monolithic state object | Everything rerenders on any change | Feature-based state slices |
| Effects in reducers | Unpredictable behavior, hard to test | Separate Effect handlers |

### Anti-Features Details

#### 1. Bidirectional Binding for Complex State
**Why avoid:**
- In chat: message list could be updated by streaming, user input, AND reconnection
- Multiple sources updating same state = race conditions and lost updates
- "Why did this value change?" becomes unanswerable

**Current codebase issue:** Services like `StreamResumeService` directly mutate state manager.

---

#### 2. Component-Held Shared State
**Why avoid:**
- When component disposes, state is lost
- Multiple instances = multiple sources of truth
- Current: `ChatContainer` holds `_messageList` reference for scroll control

**Instead:** State service outlives components, components subscribe.

---

#### 3. Direct SignalR -> Component Updates
**Why avoid:**
- SignalR notifications bypass state management
- Creates parallel update paths
- Current: `ChatNotificationHandler` updates state directly

**Instead:**
```csharp
// SignalR callback
hubConnection.On<TopicChangedNotification>("TopicChanged", notification =>
{
    // Don't update state directly, dispatch action
    dispatcher.Dispatch(new TopicChangedAction(notification));
});
```

---

#### 4. Global Re-Renders on Any State Change
**Why avoid:**
- Chat app has streaming at 50ms intervals
- Re-rendering entire component tree 20 times/second = poor performance
- Current: `OnStateChanged` event triggers `StateHasChanged()` globally

**Instead:** Subscribe to specific state slices, re-render only affected components.

---

#### 5. Monolithic State Object
**Why avoid:**
- Changing ANY property triggers re-render of ALL subscribers
- Testing requires constructing entire state
- Different update frequencies for different state (streaming vs topics)

**Current codebase:** `ChatStateManager` has topics, messages, agents, streaming, approval all in one class.

**Instead:** Feature-based state slices:
```csharp
IState<TopicsState>     // Changes rarely (create/delete topic)
IState<MessagesState>   // Changes on send/receive
IState<StreamingState>  // Changes rapidly during streaming
IState<ConnectionState> // Changes on connect/disconnect
```

---

## Feature Dependencies

```
Single Source of Truth (foundation)
         |
         v
Unidirectional Data Flow
         |
    +----+----+
    |         |
    v         v
State Change    Immutable
Notification    Updates
    |              |
    +------+-------+
           |
           v
     Selective Re-Rendering
           |
           v
     State Selectors
           |
           v
     Effects (side-effect handlers)
```

**Dependency explanation:**
1. Single source of truth must be established first
2. Unidirectional flow requires the single source
3. Notification and immutability enable selective re-rendering
4. Selectors build on selective re-rendering for fine-grained subscriptions
5. Effects extend the pattern for async operations

---

## MVP Recommendation

For MVP state management refactor, prioritize:

1. **Single source of truth** - Consolidate scattered state
2. **Unidirectional data flow** - Actions for all state mutations
3. **Clear state ownership** - Feature-based state slices
4. **State change notification** - Selective subscription per slice
5. **Immutable state updates** - Use C# records

Defer to post-MVP:
- **Effects system**: Current async handling works, optimize later
- **Optimistic updates**: Nice to have, not blocking
- **Redux DevTools integration**: Development convenience
- **State validation**: Add when bugs appear in state transitions

---

## Complexity Assessment for Chat App

| Current Pattern | Problem | Recommended Pattern | Effort |
|----------------|---------|---------------------|--------|
| `ChatStateManager` monolith | All components re-render on any change | Feature-based state slices | Medium |
| Services mutate state directly | Unpredictable update sources | Actions dispatched for all mutations | Medium |
| `OnStateChanged` global event | Global re-renders during streaming | Slice-specific subscriptions | Medium |
| Mutable `List<T>` in state | Accidental mutations possible | `ImmutableList<T>` or new list on change | Low |
| `ChatContainer` 320 lines | Mixed concerns, hard to test | Extract to smaller components + effects | High |
| SignalR handlers mutate state | Bypasses state management | Handlers dispatch actions | Low |

**Total estimated effort:** Medium-High

The biggest wins come from:
1. Feature-based state slices (stops global re-renders during streaming)
2. Actions for all mutations (makes state changes traceable)
3. Proper immutability (enables change detection optimization)

---

## Sources

**Official Microsoft Documentation:**
- [ASP.NET Core Blazor state management overview](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/?view=aspnetcore-10.0)
- [ASP.NET Core Razor component rendering](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/rendering?view=aspnetcore-10.0)
- [ASP.NET Core Blazor dependency injection](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection?view=aspnetcore-9.0)
- [Use ASP.NET Core SignalR with Blazor](https://learn.microsoft.com/en-us/aspnet/core/blazor/tutorials/signalr-blazor?view=aspnetcore-10.0)

**Community Resources:**
- [Using Fluxor for State Management in Blazor - Code Maze](https://code-maze.com/fluxor-for-state-management-in-blazor/)
- [State Management Made Easy with Fluxor in Blazor - DEV Community](https://dev.to/stevsharp/state-management-made-easy-with-fluxor-in-blazor-5028)
- [Blazor State Management: Best Practices - Infragistics](https://www.infragistics.com/blogs/blazor-state-management/)
- [State Management in Blazor: Beyond Cascading Parameters - Medium](https://medium.com/dotnet-new/state-management-in-blazor-beyond-cascading-parameters-b4bed4b5bbf5)
- [10 Architecture Mistakes Developers Make in Blazor Projects - Medium](https://medium.com/dotnet-new/10-architecture-mistakes-developers-make-in-blazor-projects-and-how-to-fix-them-e99466006e0d)
- [Blazor: The Pain of Being Coerced into Global Rerenders - Medium](https://medium.com/@mshimshon/blazor-the-pain-of-being-coerced-into-global-rerenders-or-base-components-by-state-management-67404324beb1)
- [SignalR in Blazor: From Direct Implementation to Clean Architecture - Medium](https://medium.com/@alfranklino/signalr-in-blazor-from-direct-implementation-to-clean-architecture-129b510e286d)
- [State Hasn't Changed? Why and when Blazor components re-render - Jon Hilton](https://jonhilton.net/blazor-rendering/)
- [Dependency Injection Scopes in Blazor - Thinktecture](https://www.thinktecture.com/en/blazor/dependency-injection-scopes-in-blazor/)
- [Service Lifetimes in Blazor - Chris Sainty](https://chrissainty.com/service-lifetimes-in-blazor/)
