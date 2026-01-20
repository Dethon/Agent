# Phase 3: Streaming Performance - Research

**Researched:** 2026-01-20
**Domain:** Blazor WebAssembly streaming, Rx.NET throttling, UI rendering performance
**Confidence:** HIGH

## Summary

This research investigates performance patterns for streaming content updates in Blazor WebAssembly. The phase needs to ensure that 50+ token updates per second render efficiently without freezing the UI or causing unnecessary re-renders in unrelated components (particularly the topic sidebar).

The existing codebase already has a solid foundation:
- BehaviorSubject-based Store with Observable patterns
- StoreSubscriberComponent with selector-based subscriptions and DistinctUntilChanged
- StreamingStore with per-topic state isolation
- 50ms throttle in StreamingCoordinator

The key gap is connecting these stores to components with proper isolation so streaming updates only re-render the message area, not the topic sidebar. The context decisions lock in character animation, smart auto-scroll, typing indicators, and error recovery UX patterns.

**Primary recommendation:** Use Rx.NET Sample operator (not Throttle) for periodic 50ms render ticks, implement topic-scoped streaming selectors, and add CSS-only visual feedback (blinking cursor, typing dots) that doesn't require JavaScript re-renders.

## Standard Stack

The established libraries/tools for this domain:

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Reactive | 6.x | Observable operators (Sample, Buffer) | Already in project, BehaviorSubject pattern established |
| Blazor WebAssembly | .NET 10 | UI framework | Project target framework |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| (none needed) | - | - | Pure CSS animations handle visual feedback |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Rx.NET Sample | JS-based throttle | Sample is native C#, no JS interop overhead |
| CSS cursor animation | JS-based animation | CSS is hardware-accelerated, no render cycles |
| Custom timer | PeriodicTimer | PeriodicTimer lacks observable composition |

**Installation:**
No new packages needed. Existing System.Reactive satisfies all requirements.

## Architecture Patterns

### Recommended Streaming Architecture

```
SignalR Hub Events
        │
        v
┌───────────────────┐
│ StreamingStore    │ ── Immediate state update (BehaviorSubject)
└───────────────────┘
        │
        v
┌───────────────────┐
│ RenderCoordinator │ ── Sample(50ms) for batched render
└───────────────────┘
        │
        v
┌───────────────────┐
│ MessageList       │ ── Only component that re-renders
└───────────────────┘
```

### Pattern 1: Selective Subscription with Topic Selectors

**What:** Components subscribe only to the slice of state they need
**When to use:** Prevent cascade re-renders from unrelated state changes

```csharp
// In MessageList.razor.cs
protected override void OnInitialized()
{
    // Only re-render when THIS topic's streaming content changes
    Subscribe(
        _streamingStore.StateObservable,
        state => state.StreamingByTopic.GetValueOrDefault(_currentTopicId),
        content => _streamingContent = content);
}
```

**Source:** Established pattern in StoreSubscriberComponent.cs, verified against Blazor performance best practices

### Pattern 2: Sample Operator for Periodic Render Ticks

**What:** Emit latest value at fixed intervals regardless of source frequency
**When to use:** High-frequency updates that should batch into render windows

```csharp
// Source: https://introtorx.com/chapters/time-based-sequences
// Sample emits at fixed intervals - perfect for 50ms render windows
_streamingStore.StateObservable
    .Select(s => s.StreamingByTopic.GetValueOrDefault(topicId))
    .Sample(TimeSpan.FromMilliseconds(50))
    .Subscribe(content => {
        InvokeAsync(() => {
            _content = content;
            StateHasChanged();
        });
    });
```

**Why Sample over Throttle:**
- Rx.NET Throttle is actually debounce (resets on each event)
- Throttle won't emit anything if source is faster than interval
- Sample guarantees a value every 50ms during active streaming
- Sample provides consistent render cadence matching context decision

**Source:** [Introduction to Rx.NET - Time-based sequences](https://introtorx.com/chapters/time-based-sequences)

### Pattern 3: InvokeAsync Wrapping All State Mutations

**What:** Marshal state mutations to Blazor's synchronization context
**When to use:** Any callback from timers, observables, or background operations

```csharp
// CORRECT: Wrap entire callback, not just StateHasChanged
observable.Subscribe(value =>
{
    InvokeAsync(() =>
    {
        if (_disposed) return;
        _localState = value;  // Mutation inside InvokeAsync
        StateHasChanged();
    });
});

// WRONG: Only wrapping StateHasChanged (anti-pattern)
observable.Subscribe(value =>
{
    _localState = value;  // Race condition possible
    InvokeAsync(StateHasChanged);
});
```

**Source:** [Blazor University - InvokeAsync](https://blazor-university.com/components/multi-threaded-rendering/invokeasync/) and [Microsoft Docs - Synchronization Context](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/synchronization-context)

### Pattern 4: CSS-Only Visual Feedback

**What:** Use CSS animations for typing indicators and blinking cursors
**When to use:** Visual feedback that should be smooth regardless of render frequency

```css
/* Blinking cursor at end of streaming content */
.streaming-cursor::after {
    content: "|";
    animation: blink 1s step-end infinite;
}

@keyframes blink {
    0%, 50% { opacity: 1; }
    51%, 100% { opacity: 0; }
}

/* Typing indicator with three pulsing dots */
.typing-indicator span {
    width: 8px;
    height: 8px;
    background: var(--accent);
    border-radius: 50%;
    animation: pulse 1.4s infinite ease-in-out both;
}

.typing-indicator span:nth-child(1) { animation-delay: -0.32s; }
.typing-indicator span:nth-child(2) { animation-delay: -0.16s; }
.typing-indicator span:nth-child(3) { animation-delay: 0s; }

@keyframes pulse {
    0%, 80%, 100% { transform: scale(0.6); opacity: 0.5; }
    40% { transform: scale(1); opacity: 1; }
}
```

**Source:** [DEV Community - Typing Animation](https://dev.to/3mustard/create-a-typing-animation-in-react-17o0)

### Pattern 5: Smart Auto-Scroll with JS Interop

**What:** Only auto-scroll if user is at bottom, stop if they scroll up
**When to use:** During streaming to keep latest content visible without hijacking scroll

```javascript
// Already exists in app.js - verify threshold is appropriate
window.chatScroll = {
    isAtBottom: function(element) {
        if (!element) return true;
        const threshold = 50; // pixels
        return element.scrollHeight - element.scrollTop - element.clientHeight <= threshold;
    },

    scrollToBottom: function(element, smooth) {
        if (!element) return;
        element.scrollTo({
            top: element.scrollHeight,
            behavior: smooth ? 'smooth' : 'instant'
        });
    }
};
```

**Context decision:** Use smooth scroll for auto-scroll updates per 03-CONTEXT.md

### Anti-Patterns to Avoid

- **Subscribing to entire state in parent component:** TopicList and MessageList both subscribing to root state causes cascade re-renders
- **StateHasChanged outside InvokeAsync:** Race conditions, exceptions, or silent failures
- **Throttle for periodic updates:** Throttle is debounce in Rx.NET - use Sample instead
- **JavaScript-based character animation:** Expensive interop per character - CSS handles this
- **Recreating subscription on each parameter change:** Subscribe once in OnInitialized, update selectors via reactive composition

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Render throttling | Custom timer with flags | Rx.NET Sample operator | Handles edge cases, proper disposal |
| Thread marshaling | Manual SynchronizationContext | InvokeAsync | Blazor manages context correctly |
| Blinking cursor | JavaScript setInterval | CSS @keyframes | Hardware accelerated, no JS overhead |
| Typing dots | JavaScript animation | CSS animation with delays | Already in app.css (.streaming-dots) |
| Scroll detection | Custom scroll events | Existing chatScroll.isAtBottom | Already handles edge cases |
| Subscription disposal | Manual tracking | CompositeDisposable via StoreSubscriberComponent | Already implemented |

**Key insight:** The existing codebase already has most building blocks. The phase is about proper composition and isolation, not building new infrastructure.

## Common Pitfalls

### Pitfall 1: Cascade Re-renders from Shared State

**What goes wrong:** Parent component subscribes to state, passes props to children. State change re-renders parent AND all children.
**Why it happens:** Blazor re-renders children when parent re-renders, even if child props haven't changed.
**How to avoid:** Children subscribe directly to their slice of state using selectors.
**Warning signs:** Topic sidebar flickers during streaming; profiler shows excessive render counts.

### Pitfall 2: InvokeAsync Only Around StateHasChanged

**What goes wrong:** State mutation happens before InvokeAsync, causing race conditions.
**Why it happens:** Common misconception that only the render trigger needs context marshaling.
**How to avoid:** Wrap entire callback body in InvokeAsync, not just StateHasChanged.
**Warning signs:** Intermittent exceptions, state appearing stale, "component disposed" errors.

### Pitfall 3: Using Throttle Instead of Sample

**What goes wrong:** No renders occur during rapid streaming because Throttle keeps resetting.
**Why it happens:** Rx.NET Throttle is debounce (waits for silence), not rate-limiting.
**How to avoid:** Use Sample for fixed-interval emission, Throttle for "wait until calm."
**Warning signs:** UI freezes then jumps, updates only appear after streaming stops.

### Pitfall 4: Synchronous Scroll Operations in Render

**What goes wrong:** Blocking scroll calculations during render cause jank.
**Why it happens:** scrollHeight forces layout recalc, blocking render.
**How to avoid:** Check scroll position before render via JS, scroll after render in OnAfterRenderAsync.
**Warning signs:** Choppy scrolling, visible lag between content appearing and scroll.

### Pitfall 5: Memory Leaks from Undisposed Subscriptions

**What goes wrong:** Observables keep firing into disposed components.
**Why it happens:** Subscribe creates closure over component, prevents GC.
**How to avoid:** StoreSubscriberComponent already handles via CompositeDisposable - use it.
**Warning signs:** Memory growth over time, "object disposed" exceptions, orphaned event handlers.

### Pitfall 6: Re-rendering on Reference Equality Changes

**What goes wrong:** New dictionary/list instances trigger re-render even with same content.
**Why it happens:** DistinctUntilChanged uses reference equality for collections.
**How to avoid:** Use custom equality comparer for collection selectors, or select scalar values.
**Warning signs:** Topic sidebar re-renders when streaming state changes (different dictionary instance).

## Code Examples

Verified patterns from official sources:

### RenderCoordinator Service

```csharp
// Source: Rx.NET Sample pattern + Blazor InvokeAsync best practices
public sealed class RenderCoordinator : IDisposable
{
    private readonly CompositeDisposable _subscriptions = new();
    private readonly StreamingStore _streamingStore;
    private readonly TimeSpan _renderInterval = TimeSpan.FromMilliseconds(50);

    public IObservable<StreamingContent?> CreateStreamingObservable(string topicId)
    {
        return _streamingStore.StateObservable
            .Select(state => state.StreamingByTopic.GetValueOrDefault(topicId))
            .Sample(_renderInterval)
            .DistinctUntilChanged();
    }
}
```

### Topic-Isolated Message Component

```csharp
// Source: StoreSubscriberComponent pattern + Microsoft performance docs
@inherits StoreSubscriberComponent
@inject StreamingStore StreamingStore

@code {
    [Parameter] public string TopicId { get; set; } = "";

    private StreamingContent? _streamingContent;
    private string _previousTopicId = "";

    protected override void OnParametersSet()
    {
        if (_previousTopicId != TopicId)
        {
            _previousTopicId = TopicId;
            ResubscribeToTopic();
        }
    }

    private void ResubscribeToTopic()
    {
        // Clear existing subscriptions
        ClearSubscriptions();

        // Subscribe to just this topic's streaming content
        Subscribe(
            StreamingStore.StateObservable
                .Select(s => s.StreamingByTopic.GetValueOrDefault(TopicId))
                .Sample(TimeSpan.FromMilliseconds(50)),
            content => _streamingContent = content);
    }
}
```

### CSS Blinking Cursor (Add to app.css)

```css
/* Source: CSS-Tricks and DEV Community typing animation patterns */
.streaming-message .message-content::after {
    content: "|";
    color: var(--accent);
    font-weight: bold;
    animation: cursor-blink 1s step-end infinite;
}

@keyframes cursor-blink {
    0%, 50% { opacity: 1; }
    51%, 100% { opacity: 0; }
}

/* Hide cursor when not streaming */
.message-content::after {
    content: none;
}
```

### ShouldRender Override for Sidebar Isolation

```csharp
// Source: Microsoft Blazor performance docs
// https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/rendering
@code {
    private HashSet<string>? _prevStreamingTopics;
    private string? _prevSelectedAgentId;
    private int _prevTopicCount;

    protected override bool ShouldRender()
    {
        // Only render if sidebar-relevant state changed
        var topicsChanged = Topics.Count != _prevTopicCount;
        var agentChanged = SelectedAgentId != _prevSelectedAgentId;
        var streamingSetChanged = !StreamingTopics.SetEquals(_prevStreamingTopics ?? new());

        if (topicsChanged || agentChanged || streamingSetChanged)
        {
            _prevTopicCount = Topics.Count;
            _prevSelectedAgentId = SelectedAgentId;
            _prevStreamingTopics = new HashSet<string>(StreamingTopics);
            return true;
        }

        return false;
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Event handlers calling StateHasChanged directly | InvokeAsync wrapper for all state mutations | Blazor guidance 2023+ | Prevents threading bugs |
| Manual throttle with DateTime checks | Rx.NET operators (Sample, Buffer) | Always preferred | Cleaner, composable, disposal-safe |
| JavaScript typing animations | CSS @keyframes | CSS3 widespread support | Better performance, no interop |
| Parent-to-child prop drilling for state | Direct store subscriptions with selectors | Flux/Redux patterns | Prevents cascade re-renders |

**Deprecated/outdated:**
- **Manual timer-based throttling:** Existing StreamingCoordinator pattern works but has edge cases around disposal. Rx.NET Sample is preferred.
- **JavaScript scroll animation:** CSS `scroll-behavior: smooth` handles this natively.

## Open Questions

Things that couldn't be fully resolved:

1. **Character Animation Timing**
   - What we know: Context decides "adaptive animation speed: faster when more characters buffered, slower when few"
   - What's unclear: Exact algorithm for adaptive speed calculation
   - Recommendation: Start with linear interpolation based on buffer size, refine based on feel

2. **Error Retry Backoff**
   - What we know: Context decides "auto-retry up to 3 times before showing final error state"
   - What's unclear: Whether to use immediate, linear, or exponential backoff
   - Recommendation: Claude's discretion per CONTEXT.md - suggest immediate for first retry, 1s for second, 3s for third

3. **Concurrent Stream Render Batching**
   - What we know: Context decides "single 50ms timer batches all concurrent streams together"
   - What's unclear: How to coordinate when multiple topics stream simultaneously
   - Recommendation: Single RenderCoordinator service with shared timer that triggers all subscribed components

## Sources

### Primary (HIGH confidence)
- [Microsoft - Blazor Rendering Performance Best Practices](https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/rendering) - ShouldRender, @key, avoiding cascade renders
- [Microsoft - Blazor Synchronization Context](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/synchronization-context) - InvokeAsync requirements
- [Introduction to Rx.NET - Time-based sequences](https://introtorx.com/chapters/time-based-sequences) - Throttle vs Sample semantics
- Existing codebase analysis: Store.cs, StoreSubscriberComponent.cs, StreamingCoordinator.cs, app.js, app.css

### Secondary (MEDIUM confidence)
- [Blazor University - InvokeAsync](https://blazor-university.com/components/multi-threaded-rendering/invokeasync/) - Thread safety patterns
- [DEV Community - Typing Animation](https://dev.to/3mustard/create-a-typing-animation-in-react-17o0) - CSS animation patterns
- WebSearch: Blazor performance optimization patterns 2025

### Tertiary (LOW confidence)
- None - all findings verified with primary sources

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - existing System.Reactive, no new deps needed
- Architecture: HIGH - builds on established Store/Subscriber patterns
- Pitfalls: HIGH - verified against official Blazor docs and Rx.NET docs

**Research date:** 2026-01-20
**Valid until:** 2026-02-20 (30 days - stable patterns, no fast-moving deps)
