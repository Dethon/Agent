# Technology Stack: Blazor WebAssembly Unidirectional Data Flow

**Project:** Agent WebChat Refactoring
**Researched:** 2026-01-19
**Mode:** Ecosystem + Stack Recommendation

## Executive Summary

For implementing unidirectional data flow in the existing Blazor WebAssembly chat application with SignalR, the recommended approach is a **custom lightweight store pattern using C# records** rather than adopting a full library like Fluxor. This recommendation is based on:

1. The existing codebase already has a state manager (`ChatStateManager`) that follows similar patterns
2. The application's scope is focused (chat messaging) rather than enterprise-scale
3. SignalR integration requires custom handling regardless of state library choice
4. Fluxor adds significant boilerplate overhead for this use case

## Recommended Stack

### Core Pattern: Custom Immutable Store with Records

| Component | Technology | Version | Confidence | Rationale |
|-----------|------------|---------|------------|-----------|
| State Containers | C# `record` types | Built-in (.NET 10) | HIGH | Immutability via `with` keyword, structural equality, minimal boilerplate |
| State Notification | `INotifyPropertyChanged` or `Action` events | Built-in | HIGH | Standard .NET pattern, works with Blazor's rendering |
| DI Lifetime | `AddSingleton<T>()` for WASM | Built-in | HIGH | WebAssembly runs in-browser, single-user context |
| Change Detection | Manual `StateHasChanged()` calls | Blazor built-in | HIGH | Required regardless of state approach |

**Why NOT Fluxor:**
- Adds ~300KB to WASM bundle size
- Requires learning Redux concepts (Actions, Reducers, Effects)
- Boilerplate overhead for feature/state registration
- Performance testing shows higher memory usage vs simpler patterns
- Overkill for single-domain application (chat)

**When Fluxor WOULD be appropriate:**
- Multi-domain enterprise applications
- Team already familiar with Redux
- Need time-travel debugging
- Complex state interdependencies across unrelated features

### SignalR Integration Layer

| Component | Technology | Version | Confidence | Rationale |
|-----------|------------|---------|------------|-----------|
| SignalR Client | `Microsoft.AspNetCore.SignalR.Client` | 10.x | HIGH | Already in use, official Microsoft package |
| Hub Connection | `HubConnectionBuilder` | 10.x | HIGH | Standard approach, supports auto-reconnect |
| Message Handlers | `connection.On<T>()` dispatch to stores | N/A | HIGH | Pattern: SignalR event -> Store action -> UI update |

### Supporting Libraries (Already in Project)

| Library | Purpose | Keep/Remove |
|---------|---------|-------------|
| `System.Reactive` | Observable streams for throttling | KEEP - useful for render throttling |
| None additional | - | No new dependencies needed |

## Architecture Pattern: Event-Driven Stores

### Recommended Pattern

```
SignalR Hub -> HubEventDispatcher -> Domain Stores -> UI Components
     ^                                      |
     |                                      v
     +<--------- User Actions <-----------+
```

### State Flow

```
1. SignalR receives message chunk
2. HubEventDispatcher routes to appropriate store method
3. Store updates immutable state (record with {...})
4. Store raises OnStateChanged event
5. Subscribed components call StateHasChanged()
6. Blazor re-renders affected components
```

### Store Structure (Recommended)

```csharp
// Immutable state as record
public sealed record ChatState(
    ImmutableList<ChatMessage> Messages,
    ChatMessage? StreamingMessage,
    bool IsStreaming,
    string? Error);

// Store with actions and events
public sealed class ChatStore
{
    private ChatState _state = new([], null, false, null);

    public ChatState State => _state;
    public event Action? OnStateChanged;

    // Actions (methods that return new state)
    public void AddMessage(ChatMessage message)
    {
        _state = _state with
        {
            Messages = _state.Messages.Add(message)
        };
        NotifyStateChanged();
    }

    public void UpdateStreamingMessage(ChatMessage? message)
    {
        _state = _state with { StreamingMessage = message };
        // Note: May skip NotifyStateChanged for throttled rendering
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}
```

### SignalR Event Dispatcher (Recommended)

```csharp
public sealed class HubEventDispatcher(
    ChatStore chatStore,
    TopicStore topicStore,
    HubConnection connection)
{
    public void Initialize()
    {
        connection.On<ChatStreamMessage>("ReceiveChunk", chunk =>
        {
            chatStore.AppendChunk(chunk);
        });

        connection.On<ToolApprovalRequest>("RequestApproval", request =>
        {
            chatStore.SetPendingApproval(request);
        });

        // Route all hub events to appropriate stores
    }
}
```

## Alternatives Considered

### Option 1: Fluxor (NOT Recommended for This Project)

| Aspect | Assessment |
|--------|------------|
| Version | 6.9.0 (November 2025) |
| .NET Support | .NET 6+ |
| Downloads | 2.8M+ |
| Maturity | HIGH |
| Bundle Size | ~300KB additional |
| Learning Curve | MEDIUM-HIGH (Redux concepts) |

**Pros:**
- Battle-tested Redux implementation
- Redux DevTools integration
- Strong community
- Enforced immutability
- Time-travel debugging

**Cons:**
- Significant boilerplate for Actions/Reducers/Effects
- Memory overhead (state snapshots)
- Overkill for focused applications
- No built-in SignalR integration (still manual)

**Verdict:** Use for enterprise multi-domain apps, not for this chat application.

### Option 2: TimeWarp.State (NOT Recommended)

| Aspect | Assessment |
|--------|------------|
| Version | 11.0.3 (June 2025) |
| .NET Support | .NET 8+ |
| Pattern | Flux via MediatR pipeline |
| Maturity | MEDIUM |

**Pros:**
- Fully async handlers
- Middleware pipeline (like ASP.NET)
- Lower memory than Fluxor (per comparisons)

**Cons:**
- Requires MediatR knowledge
- Less community adoption than Fluxor
- Still adds complexity for simple use cases
- No significant advantage for chat application

**Verdict:** Consider if already using MediatR; otherwise skip.

### Option 3: EasyAppDev.Blazor.Store (MONITOR)

| Aspect | Assessment |
|--------|------------|
| Version | 2.0.0 |
| .NET Support | .NET 8/9/10 |
| Pattern | Zustand-inspired |
| Maturity | LOW (newer library) |

**Pros:**
- Built-in SignalR collaboration support
- Zustand-like API (simpler than Redux)
- Cross-tab sync
- Undo/redo built-in

**Cons:**
- Newer library, less proven
- Documentation still maturing
- May be overkill for this use case

**Verdict:** Monitor for future projects; too new for production refactor.

### Option 4: Custom Store Pattern (RECOMMENDED)

**Pros:**
- Zero new dependencies
- Full control over implementation
- Optimized for exact use case
- Matches existing codebase patterns
- Easy SignalR integration

**Cons:**
- No Redux DevTools
- No time-travel debugging
- Must implement patterns manually

**Verdict:** Best fit for this project's scope and existing architecture.

## SignalR + State Integration Pattern

### The Challenge

SignalR events arrive asynchronously and must flow into the state system without creating bidirectional coupling. The existing `StreamingCoordinator` (416 lines) mixes:
- SignalR message handling
- State updates
- Render throttling
- Buffer management

### Recommended Separation

| Concern | Component | Responsibility |
|---------|-----------|----------------|
| Connection | `ChatConnectionService` | Hub lifecycle, reconnection (EXISTS) |
| Event Routing | `HubEventDispatcher` (NEW) | Route hub events to stores |
| State | Domain Stores | Hold immutable state, emit change events |
| Coordination | `StreamingCoordinator` | Orchestrate multi-message flows (REFACTOR) |
| Rendering | Components | Subscribe to stores, call StateHasChanged |

### Event-to-Store Flow

```
HubConnection.On("ReceiveChunk")
    |
    v
HubEventDispatcher.HandleChunk(chunk)
    |
    v
ChatStore.AppendStreamingContent(chunk.Content)  // Immutable update
    |
    v
ChatStore.OnStateChanged?.Invoke()
    |
    v
ChatPage.HandleStateChanged()
    |
    v
await InvokeAsync(StateHasChanged)
```

### Throttling Strategy

The existing `StreamingCoordinator` implements render throttling (50ms). This should move to a dedicated concern:

```csharp
public sealed class RenderThrottler
{
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(50);
    private DateTime _lastRender = DateTime.MinValue;

    public bool ShouldRender()
    {
        var now = DateTime.UtcNow;
        if (now - _lastRender >= _interval)
        {
            _lastRender = now;
            return true;
        }
        return false;
    }
}
```

## What NOT to Use

| Technology | Reason |
|------------|--------|
| Fluxor | Overkill for chat-focused app, adds bundle size |
| TimeWarp.State | Adds MediatR dependency, complexity without benefit |
| Redux.NET | Abandoned/unmaintained |
| Blazor-Redux | Abandoned/unmaintained |
| MobX ports | Limited .NET ecosystem support |
| `localStorage` for state | Not needed for transient chat state |
| Cascading Parameters for app state | Poor performance at scale, anti-pattern for global state |

## Migration Path from Current Architecture

### Current State (Problems)

1. `ChatStateManager` - 272 lines, mutable dictionaries, many concerns
2. `StreamingCoordinator` - 416 lines, mixed responsibilities
3. `ChatConnectionService` - Clean, keep as-is
4. State scattered across services

### Target State

1. **Immutable State Records** - Separate records for each domain
2. **Domain Stores** - One store per bounded context (Chat, Topic, Agent)
3. **Hub Event Dispatcher** - Single point for SignalR -> Store routing
4. **Simplified Coordinator** - Only orchestration, no state ownership

### Phase Approach

1. **Phase 1:** Extract immutable state records from `ChatStateManager`
2. **Phase 2:** Create domain-specific stores (ChatStore, TopicStore, AgentStore)
3. **Phase 3:** Implement `HubEventDispatcher` for SignalR routing
4. **Phase 4:** Refactor `StreamingCoordinator` to use stores
5. **Phase 5:** Update components to subscribe to stores

## Confidence Assessment

| Recommendation | Confidence | Verification |
|----------------|------------|--------------|
| Use custom stores over Fluxor | HIGH | Microsoft docs recommend built-in for simple apps; Fluxor docs recommend for complex apps |
| C# records for immutable state | HIGH | Official .NET pattern, proven in production |
| SignalR event dispatcher pattern | HIGH | Documented in Fluxor+SignalR tutorials, common pattern |
| Singleton DI for WASM stores | HIGH | Microsoft docs, standard practice |
| Avoid Cascading Parameters for global state | HIGH | Microsoft docs warn about performance |

## Sources

### Primary (HIGH Confidence)
- [Microsoft Blazor State Management (.NET 10)](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/?view=aspnetcore-10.0)
- [Fluxor GitHub Repository](https://github.com/mrpmorris/Fluxor)
- [TimeWarp.State GitHub](https://github.com/TimeWarpEngineering/timewarp-state)

### Secondary (MEDIUM Confidence)
- [Fluxor for State Management - Code Maze](https://code-maze.com/fluxor-for-state-management-in-blazor/)
- [Fluxor + SignalR Tutorial - DEV Community](https://dev.to/mr_eking/advanced-blazor-state-management-using-fluxor-part-7-client-to-client-comms-with-signalr-4p9)
- [AppState Pattern - Medium](https://medium.com/@robhutton8/appstate-pattern-in-net-and-blazor-efficient-state-management-for-modern-applications-f3150d35b79a)
- [Blazor State Management Guide 2025 - Toxigon](https://toxigon.com/blazor-state-management-guide)
- [EasyAppDev.Blazor.Store GitHub](https://github.com/mashrulhaque/EasyAppDev.Blazor.Store)

### Community (LOW Confidence - Patterns Only)
- [Fluxor Performance Comparison](https://www.igniscor.com/post/blazor-state-blazor-fluxor-performance)
- [Redux Pattern in Blazor](https://steven-giesel.com/blogPost/d62f8d80-ba9b-47c3-9040-8e17affda513)
