# Phase 1: State Foundation - Research

**Researched:** 2026-01-20
**Domain:** Unidirectional state management infrastructure for Blazor WebAssembly
**Confidence:** HIGH

## Summary

This phase establishes the core state management infrastructure for the WebChat.Client Blazor WebAssembly application. The goal is to create a foundation for unidirectional data flow using custom stores with C# records, IObservable-based subscriptions via System.Reactive, and a StoreSubscriberComponent base class for automatic subscription management.

The current codebase has a monolithic `ChatStateManager` (272 lines) that mixes state storage with business logic, uses mutable dictionaries, and triggers global re-renders via a single `OnStateChanged` event. Components like `ChatContainer.razor` (320 lines) manually subscribe/unsubscribe from this event and handle complex state logic inline.

**Primary recommendation:** Create a generic `Store<TState>` class using `BehaviorSubject<TState>` for IObservable subscriptions, an `IAction` marker interface for typed actions, and a `StoreSubscriberComponent` base class that handles subscription lifecycle automatically. Feature-specific state slices will be added in Phase 2.

## Standard Stack

The established libraries/tools for this domain:

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| C# Records | Built-in (.NET 10) | Immutable state representation | `with` expressions for non-destructive updates, structural equality for change detection |
| System.Reactive | 6.1.0 | IObservable/BehaviorSubject for subscriptions | Official Rx.NET, composable operators, auto-disposal patterns |
| ComponentBase | Built-in (Blazor) | Base class for Razor components | Required by Blazor, StoreSubscriberComponent extends this |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Collections.Immutable | Built-in (.NET 10) | Immutable collections for state | When state contains lists that change frequently |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| BehaviorSubject | Action event | Action event is simpler but lacks composability (throttling, combining streams) |
| System.Collections.Immutable | Regular List with `ToList()` | Immutable collections are safer but have allocation overhead; use regular List + `with` for small collections |

**Installation:**
```bash
dotnet add WebChat.Client package System.Reactive --version 6.1.0
```

**Note:** System.Reactive is already referenced in the project planning documents but not yet in WebChat.Client.csproj. It needs to be added.

## Architecture Patterns

### Recommended Project Structure
```
WebChat.Client/
├── State/
│   ├── Store.cs                    # Generic Store<TState> implementation
│   ├── IAction.cs                  # Marker interface for actions
│   ├── IDispatcher.cs              # Dispatcher interface
│   ├── Dispatcher.cs               # Dispatcher implementation
│   ├── StoreSubscriberComponent.cs # Base component with auto-subscription
│   └── Selectors.cs                # Memoization infrastructure (if needed)
└── ... (existing structure)
```

Phase 2 will add feature folders:
```
WebChat.Client/
├── State/
│   ├── Topics/
│   │   ├── TopicsState.cs
│   │   ├── TopicsActions.cs
│   │   ├── TopicsReducers.cs
│   │   └── TopicsSelectors.cs
│   ├── Messages/
│   │   └── ...
│   └── ... (infrastructure files at root)
```

### Pattern 1: Generic Store with BehaviorSubject

**What:** A generic `Store<TState>` class that holds immutable state, exposes it via IObservable, and updates via reducer functions.

**When to use:** All state containers in the application.

**Example:**
```csharp
// Source: Verified pattern from System.Reactive BehaviorSubject documentation
public sealed class Store<TState> : IDisposable where TState : class
{
    private readonly BehaviorSubject<TState> _subject;

    public Store(TState initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        _subject = new BehaviorSubject<TState>(initialState);
    }

    public TState State => _subject.Value;

    public IObservable<TState> StateObservable => _subject.AsObservable();

    public void Dispatch<TAction>(TAction action, Func<TState, TAction, TState> reducer)
        where TAction : IAction
    {
        var newState = reducer(State, action);
        _subject.OnNext(newState);
    }

    public void Dispose() => _subject.Dispose();
}
```

### Pattern 2: Action as Record Type

**What:** Actions defined as immutable record types implementing a marker interface.

**When to use:** All state mutations dispatched through the system.

**Example:**
```csharp
// Marker interface for type safety
public interface IAction { }

// Actions grouped by feature (Phase 2 pattern, shown for reference)
public static class TopicsActions
{
    public record TopicsLoaded(IReadOnlyList<StoredTopic> Topics) : IAction;
    public record TopicSelected(StoredTopic? Topic) : IAction;
    public record TopicAdded(StoredTopic Topic) : IAction;
    public record TopicRemoved(string TopicId) : IAction;
}
```

### Pattern 3: StoreSubscriberComponent Base Class

**What:** A base component that manages IObservable subscriptions and automatically disposes them.

**When to use:** Any component that needs to react to store state changes.

**Example:**
```csharp
// Source: Microsoft Blazor component disposal docs + System.Reactive patterns
public abstract class StoreSubscriberComponent : ComponentBase, IDisposable
{
    private readonly CompositeDisposable _subscriptions = new();

    protected void Subscribe<T>(IObservable<T> observable, Action<T> onNext)
    {
        var subscription = observable.Subscribe(value =>
        {
            InvokeAsync(() =>
            {
                onNext(value);
                StateHasChanged();
            });
        });
        _subscriptions.Add(subscription);
    }

    // Selective subscription - only re-render when selector output changes
    protected void Subscribe<TState, TSelected>(
        IObservable<TState> stateObservable,
        Func<TState, TSelected> selector,
        Action<TSelected> onNext)
    {
        var subscription = stateObservable
            .Select(selector)
            .DistinctUntilChanged()
            .Subscribe(value =>
            {
                InvokeAsync(() =>
                {
                    onNext(value);
                    StateHasChanged();
                });
            });
        _subscriptions.Add(subscription);
    }

    public virtual void Dispose()
    {
        _subscriptions.Dispose();
    }
}
```

### Pattern 4: Dispatcher Service

**What:** A centralized service that routes actions to the appropriate store reducers.

**When to use:** Decouples components from specific stores, enables action logging/middleware later.

**Example:**
```csharp
public interface IDispatcher
{
    void Dispatch<TAction>(TAction action) where TAction : IAction;
}

// Simple implementation for Phase 1 (middleware added in later phases)
public sealed class Dispatcher : IDispatcher
{
    // Store registrations will be added in Phase 2 when feature stores are created
    private readonly Dictionary<Type, Action<IAction>> _handlers = new();

    public void RegisterHandler<TAction>(Action<TAction> handler) where TAction : IAction
    {
        _handlers[typeof(TAction)] = action => handler((TAction)action);
    }

    public void Dispatch<TAction>(TAction action) where TAction : IAction
    {
        if (_handlers.TryGetValue(typeof(TAction), out var handler))
        {
            handler(action);
        }
    }
}
```

### Pattern 5: Immutable State with Records

**What:** State represented as C# records with init-only properties.

**When to use:** All state objects.

**Example:**
```csharp
// Source: C# record immutability patterns
public sealed record ExampleState
{
    public IReadOnlyList<string> Items { get; init; } = [];
    public string? SelectedItem { get; init; }
    public bool IsLoading { get; init; }
}

// Update using `with` expression
var newState = state with { IsLoading = true };
var withNewItem = state with { Items = state.Items.Append(item).ToList() };
```

### Anti-Patterns to Avoid

- **Mutable state:** Never use `List<T>` directly in state; use `IReadOnlyList<T>` and create new lists on update
- **Direct store mutation:** Never modify state outside of reducers
- **Missing InvokeAsync:** Always wrap `StateHasChanged()` in `InvokeAsync()` when called from subscription callbacks
- **Global re-renders:** Don't subscribe to entire state if only a slice is needed; use selectors with `DistinctUntilChanged()`
- **Manual subscription management:** Don't manage subscriptions manually in components; use `StoreSubscriberComponent`

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Observable state | Custom event aggregator | BehaviorSubject from System.Reactive | Handles replay of latest value to new subscribers, disposal, error handling |
| Subscription disposal | Manual tracking in List | CompositeDisposable | Thread-safe, handles multiple subscriptions, single Dispose() call |
| Change detection | Custom equality comparison | Record structural equality + DistinctUntilChanged() | Records have built-in structural equality; Rx handles the comparison |
| Thread-safe UI updates | Direct StateHasChanged() | InvokeAsync wrapper | Blazor's synchronization context requires this for callbacks |
| Immutable collections | Custom immutable wrappers | IReadOnlyList<T> + new List() or ImmutableList<T> | Built-in, well-tested, optimized |

**Key insight:** System.Reactive provides battle-tested primitives for reactive state management. The BehaviorSubject + CompositeDisposable + DistinctUntilChanged combination handles 90% of the complexity of state subscriptions.

## Common Pitfalls

### Pitfall 1: Calling StateHasChanged from Wrong Context

**What goes wrong:** `StateHasChanged()` called from an IObservable subscription callback throws or causes unpredictable behavior.

**Why it happens:** IObservable callbacks may run on a different synchronization context than Blazor's render thread.

**How to avoid:** Always wrap in `InvokeAsync()`:
```csharp
observable.Subscribe(value =>
{
    InvokeAsync(() =>
    {
        // Update local state
        StateHasChanged();
    });
});
```

**Warning signs:** Exceptions about accessing render tree from wrong thread, UI not updating.

### Pitfall 2: Memory Leaks from Unsubscribed Observables

**What goes wrong:** Component is disposed but subscription continues, holding references to the component.

**Why it happens:** IObservable subscriptions are not automatically disposed when a component is removed.

**How to avoid:** Use `CompositeDisposable` in a base class and dispose in `Dispose()`:
```csharp
public void Dispose()
{
    _subscriptions.Dispose(); // Disposes all added subscriptions
}
```

**Warning signs:** Memory growth over time, callbacks executing after component disposal.

### Pitfall 3: Excessive Re-renders During Streaming

**What goes wrong:** Every streaming chunk triggers full component tree re-render.

**Why it happens:** Subscribing to entire state instead of specific slices.

**How to avoid:** Use selectors with `DistinctUntilChanged()`:
```csharp
stateObservable
    .Select(s => s.StreamingMessage)
    .DistinctUntilChanged()
    .Subscribe(msg => { /* only fires when StreamingMessage changes */ });
```

**Warning signs:** Laggy UI during streaming, browser CPU spikes.

### Pitfall 4: Shallow Immutability of Records

**What goes wrong:** Nested reference types in records can still be mutated.

**Why it happens:** `with` expressions copy references, not deep clones.

**How to avoid:** Use immutable collections or create new instances for nested objects:
```csharp
// BAD: state.Messages is the same list reference
var bad = state with { IsLoading = false };
state.Messages.Add(newMessage); // Mutates both!

// GOOD: Create new list
var good = state with
{
    Messages = state.Messages.Append(newMessage).ToList()
};
```

**Warning signs:** State changes affecting other state snapshots, unpredictable UI.

### Pitfall 5: BehaviorSubject Disposed Too Early

**What goes wrong:** Accessing `State` property after store disposal throws.

**Why it happens:** Store disposed before all component subscriptions complete.

**How to avoid:** Ensure store outlives all subscribers (Scoped lifetime in DI), dispose subscriptions before stores.

**Warning signs:** ObjectDisposedException on state access.

## Code Examples

Verified patterns from official sources:

### Creating and Registering the Store Infrastructure

```csharp
// In Program.cs - DI registration
// Source: Microsoft Blazor DI docs, Scoped = Singleton in WASM
builder.Services.AddScoped<IDispatcher, Dispatcher>();
// Feature stores added in Phase 2
```

### Component Subscribing to State

```csharp
// Source: Microsoft Blazor component disposal + System.Reactive patterns
@inherits StoreSubscriberComponent
@inject Store<TopicsState> TopicsStore

@code {
    private IReadOnlyList<StoredTopic> _topics = [];

    protected override void OnInitialized()
    {
        // Auto-disposed subscription via base class
        Subscribe(
            TopicsStore.StateObservable,
            state => state.Topics,
            topics => _topics = topics
        );
    }
}
```

### Dispatching Actions

```csharp
// Source: Redux/Flux pattern adapted for C#
@inject IDispatcher Dispatcher

@code {
    private async Task HandleTopicSelected(StoredTopic topic)
    {
        Dispatcher.Dispatch(new TopicsActions.TopicSelected(topic));

        // Side effects (API calls) still happen in component for Phase 1
        // Effects system can be added in later phases
        if (!HasMessagesForTopic(topic.TopicId))
        {
            var history = await TopicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
            Dispatcher.Dispatch(new MessagesActions.MessagesLoaded(topic.TopicId, history));
        }
    }
}
```

### Reducer Function

```csharp
// Source: Redux reducer pattern
public static class TopicsReducers
{
    public static TopicsState Reduce(TopicsState state, IAction action) => action switch
    {
        TopicsActions.TopicsLoaded a => state with { Topics = a.Topics },
        TopicsActions.TopicSelected a => state with { SelectedTopic = a.Topic },
        TopicsActions.TopicAdded a => state with
        {
            Topics = state.Topics.Append(a.Topic).ToList()
        },
        TopicsActions.TopicRemoved a => state with
        {
            Topics = state.Topics.Where(t => t.TopicId != a.TopicId).ToList()
        },
        _ => state
    };
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Action event + manual subscription | IObservable with CompositeDisposable | Blazor matured | Cleaner disposal, composable streams |
| Mutable class state | C# records with `with` expressions | C# 9+ (2020) | Built-in immutability, structural equality |
| Global OnStateChanged event | Selective subscriptions with DistinctUntilChanged | Rx best practice | Prevents unnecessary re-renders |

**Deprecated/outdated:**
- **Cascading parameters for app state:** Poor performance at scale, implicit dependencies
- **Component-local state for shared data:** State lost on disposal, inconsistent across components

## Open Questions

Things that couldn't be fully resolved:

1. **Memoized Selectors Implementation**
   - What we know: Fluxor has `[MemoizedSelector]` attribute; manual memoization is straightforward with Func caching
   - What's unclear: Whether built-in memoization is needed in Phase 1 or can be deferred
   - Recommendation: Defer memoization infrastructure to Phase 3 (performance optimization); `DistinctUntilChanged()` provides sufficient optimization for Phase 1

2. **Store Registration Pattern**
   - What we know: Dispatcher needs to know which store handles which action types
   - What's unclear: Best pattern for registering multiple feature stores with single dispatcher
   - Recommendation: Start with direct store injection in components for Phase 1; centralized dispatcher routing added in Phase 2 when multiple stores exist

3. **Auto-Subscribe vs Explicit Subscribe in StoreSubscriberComponent**
   - What we know: Auto-subscribe reduces boilerplate; explicit gives more control
   - What's unclear: Which pattern fits better with existing component structure
   - Recommendation: Use explicit Subscribe() calls for clarity; components choose what to subscribe to

## Sources

### Primary (HIGH confidence)
- [Microsoft Blazor Component Disposal](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/component-disposal) - IDisposable patterns, event unsubscription
- [System.Reactive BehaviorSubject](https://learn.microsoft.com/en-us/previous-versions/dotnet/reactive-extensions/hh211949(v=vs.103)) - BehaviorSubject API and semantics
- [C# Records Reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/record) - Immutability, `with` expressions
- [System.Reactive NuGet 6.1.0](https://www.nuget.org/packages/system.reactive/) - Current stable version

### Secondary (MEDIUM confidence)
- [Blazor + Reactive Extensions Pattern](https://blog.vyvojari.dev/blazor-reactive-extensions/) - TakeUntil disposal pattern
- [C# Record Immutable Workflows](https://medium.com/@mohsho10/records-are-not-just-dtos-designing-smart-immutable-workflows-with-c-record-types-4d56f504b564) - State management with records
- [Immutable Update Patterns](https://blog.stephencleary.com/2023/12/the-joy-of-immutable-update-patterns.html) - Unidirectional data flow with records

### Tertiary (LOW confidence - patterns only)
- [Fluxor Memoized Selectors](https://isitvritra101.medium.com/fluxor-in-blazor-a-critical-analysis-for-senior-net-developers-a2e6b97d38db) - Selector pattern reference
- [Redux Deriving Data with Selectors](https://redux.js.org/usage/deriving-data-selectors) - Conceptual reference for selector patterns

### Codebase Analysis (HIGH confidence)
- `WebChat.Client/Services/State/ChatStateManager.cs` - Current state management (272 lines)
- `WebChat.Client/Components/Chat/ChatContainer.razor` - Current component pattern (320 lines)
- `WebChat.Client/Program.cs` - Current DI registration
- `.planning/research/STACK.md` - Prior research on custom stores vs Fluxor
- `.planning/research/FEATURES.md` - Feature analysis for state management

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - System.Reactive and C# records are official Microsoft technologies with extensive documentation
- Architecture patterns: HIGH - Patterns verified against Microsoft Blazor docs and established Redux/Flux principles
- Pitfalls: HIGH - Based on official Blazor component lifecycle documentation and reactive programming best practices

**Research date:** 2026-01-20
**Valid until:** 2026-02-20 (stable technologies, 30-day validity)

---

*Phase: 01-state-foundation*
*Research complete: 2026-01-20*
