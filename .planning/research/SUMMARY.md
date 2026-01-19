# Research Summary: WebChat State Management Refactoring

**Project:** Agent WebChat Client Refactoring
**Synthesized:** 2026-01-19
**Overall Confidence:** HIGH

## Executive Summary

The WebChat client refactoring requires implementing unidirectional data flow to replace the current scattered state management across `ChatStateManager` (272 lines), `StreamingCoordinator` (416 lines), and `StreamResumeService`. Research strongly recommends a **custom lightweight Flux-inspired pattern using C# records** rather than adopting Fluxor or other libraries. This approach aligns with the codebase's existing patterns, avoids 300KB+ bundle size increase, and provides full control over SignalR integration which requires custom handling regardless of library choice.

The architecture should follow a clear separation: immutable state records, domain-specific stores (Chat, Topic, Connection), a hub event dispatcher for SignalR routing, and pure reducer functions for state transitions. The current throttled rendering pattern (50ms) must be preserved to prevent UI freezes during streaming. Critical pitfalls include memory leaks from event subscription mismanagement, race conditions from improper `InvokeAsync` usage, and re-render cascades that can freeze the UI at 50+ updates/second.

The recommended approach delivers the benefits of predictable state management without the overhead of external libraries. The focused domain (chat messaging) doesn't warrant enterprise-scale patterns. The existing test suite (60+ tests for state management) provides a safety net but must be adapted, not deleted, during refactoring.

## Key Findings

### From STACK.md

| Technology | Recommendation | Rationale |
|------------|----------------|-----------|
| State containers | C# `record` types | Built-in immutability via `with` keyword, structural equality |
| State notification | `Action` events | Standard .NET pattern, works with Blazor rendering |
| DI lifetime | `AddSingleton` for WASM | Single-user context in WebAssembly |
| SignalR client | Keep existing `Microsoft.AspNetCore.SignalR.Client` 10.x | Already in use, official package |
| Libraries | Custom stores over Fluxor | Fluxor adds 300KB bundle, overkill for chat domain |

**Critical version requirement:** .NET 10 (target framework)

**What NOT to use:**
- Fluxor (bundle size, complexity overhead)
- Cascading Parameters for global state (poor performance)
- TimeWarp.State (adds MediatR dependency)
- Redux.NET/Blazor-Redux (abandoned)

### From FEATURES.md

**Table Stakes (Must Have):**
1. Single source of truth - Consolidate scattered state into centralized stores
2. Unidirectional data flow - Action -> Reducer -> State -> UI pattern
3. Clear state ownership - Feature-based state slices (Topics, Messages, Streaming, Connection)
4. State change notification - Selective subscription per slice (not global)
5. Immutable state updates - C# records with `ImmutableList<T>`
6. Thread-safe mutations - All state access inside `InvokeAsync`
7. Separation of UI and state logic - Components ~50-100 lines, render only

**Differentiators (Should Have):**
- Selective re-rendering (critical for streaming performance)
- State selectors with memoization
- Effects system for async operations

**Defer to v2+:**
- Redux DevTools integration
- Time-travel debugging
- Optimistic updates
- State validation middleware

### From ARCHITECTURE.md

**Target Architecture Pattern:**

```
SignalR Hub -> HubEventDispatcher -> Reducers -> Stores -> UI Components
                                                   ^            |
                                                   |            v
                                                   +<-- Actions --+
```

**Component Boundaries:**

| Component | Responsibility |
|-----------|---------------|
| `ChatState` (record) | Immutable state container |
| `IChatStore` | State + event notification + dispatch |
| `IChatAction` | Intent description records |
| `IChatReducer` | Pure functions: (State, Action) -> State |
| Effects | Side effects (SignalR calls, persistence) |
| `SignalREventHandler` | Routes hub events to actions |
| `StoreSubscriberComponent` | Base class with subscription lifecycle |

**State Slices to Create:**
- `TopicsState` - Changes rarely (create/delete topic)
- `MessagesState` - Changes on send/receive
- `StreamingState` - Changes rapidly during streaming (needs throttling)
- `ConnectionState` - Changes on connect/disconnect
- `ApprovalState` - Modal visibility and request data

**Clean Architecture Alignment:**
- Move `INotifier` from Domain to Infrastructure (it's SignalR-specific)

### From PITFALLS.md

**Critical Pitfalls (Cause Rewrites/Production Bugs):**

| Pitfall | Phase Risk | Prevention |
|---------|------------|------------|
| Event handler memory leaks | All phases | Every `+=` must have `-=` in Dispose |
| InvokeAsync anti-pattern | Phase 2 | Wrap ALL state mutations, not just StateHasChanged |
| Streaming state loss on reconnect | Phase 3 | Map every state field before coding |
| Re-render cascade during streaming | Phase 2 | Preserve throttled render pattern (50ms) |
| Breaking existing tests | All phases | Adapt tests, never delete coverage |

**Moderate Pitfalls:**
- Two-way binding conflicts with immutable state (use local component state for inputs)
- Over-engineering initial store structure (start with ONE store, split when needed)
- Losing approval flow during refactor (document flow step-by-step before refactoring)
- Throttle logic duplication (keep in single service, not per-store)
- SignalR event subscription ordering (guard against null/disposed)

**Critical State to Preserve from Current Implementation:**
```csharp
// These MUST migrate to new stores
HashSet<string> _streamingTopics
HashSet<string> _resumingTopics
Dictionary<string, ChatMessageModel?> _streamingMessageByTopic
```

## Implications for Roadmap

### Suggested Phase Structure

**Phase 1: State Foundation**
- Create immutable `ChatState` record with all current state fields
- Implement `IChatStore` interface and basic `ChatStore` implementation
- Define core action types (SelectTopic, AddMessage, StartStreaming, etc.)
- Create `StoreSubscriberComponent` base class
- **Delivers:** Architectural foundation, no behavior change yet
- **Features:** Single source of truth, immutable state
- **Pitfalls to avoid:** Over-engineering (start simple), breaking existing tests
- **Research flag:** Standard patterns, no additional research needed

**Phase 2: Reducer Implementation**
- Implement reducers for each state slice
- Add selective notification (streaming state separate from topic state)
- Preserve 50ms throttled render pattern for streaming updates
- Wire `InvokeAsync` correctly for thread safety
- **Delivers:** State transition logic isolated and testable
- **Features:** Unidirectional flow, clear state ownership, thread safety
- **Pitfalls to avoid:** Re-render cascade, InvokeAsync anti-pattern
- **Research flag:** Standard patterns, no additional research needed

**Phase 3: Migration**
- Replace `ChatStateManager` usage in components with `IChatStore`
- Create `HubEventDispatcher` to route SignalR events to actions
- Move streaming logic from `StreamingCoordinator` to effects + reducers
- Migrate `StreamResumeService` reconnection logic to effect
- **Delivers:** Full unidirectional flow, SignalR integrated
- **Features:** Effects system, SignalR integration
- **Pitfalls to avoid:** Streaming state loss, memory leaks, approval flow breakage
- **Research flag:** May need `/gsd:research-phase` for SignalR reconnection edge cases

**Phase 4: Component Simplification**
- Refactor `ChatContainer.razor` (320 lines) into smaller components
- Components dispatch actions, render from store state
- Remove direct service calls from components
- **Delivers:** Clean, testable components
- **Features:** Separation of UI and state logic
- **Pitfalls to avoid:** Breaking existing tests
- **Research flag:** Standard patterns, no additional research needed

**Phase 5: Cleanup and Documentation**
- Delete `ChatStateManager`, `StreamingCoordinator` (replaced by stores/reducers)
- Move `INotifier` from Domain to Infrastructure
- Update DI registrations
- Verify all tests pass with same coverage
- **Delivers:** Clean architecture, reduced technical debt
- **Pitfalls to avoid:** Losing test coverage
- **Research flag:** None needed

### Rationale for Phase Order

1. **Foundation first** - Store infrastructure needed before migration
2. **Reducers before migration** - Pure functions can be unit tested in isolation
3. **Migration is largest phase** - Highest risk, needs foundation in place
4. **Components last** - Can only simplify after state management is centralized
5. **Cleanup deferred** - Don't delete old code until new code is proven

### Dependencies

```
Phase 1 (Foundation) -> Phase 2 (Reducers) -> Phase 3 (Migration) -> Phase 4 (Components) -> Phase 5 (Cleanup)
```

No phases can run in parallel; each builds on the previous.

## Research Flags

| Phase | Research Needed | Reason |
|-------|-----------------|--------|
| Phase 1 | None | Standard C# record/store patterns |
| Phase 2 | None | Standard reducer patterns |
| Phase 3 | **MAYBE** | SignalR reconnection edge cases if issues arise |
| Phase 4 | None | Standard component refactoring |
| Phase 5 | None | Straightforward cleanup |

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | Microsoft docs, established patterns, multiple sources agree |
| Features | HIGH | Official Microsoft guidance, community best practices |
| Architecture | HIGH | Flux pattern well-documented, multiple implementation examples |
| Pitfalls | HIGH | Sources from production experience, Microsoft docs, known Blazor issues |

### Gaps to Address During Planning

1. **Exact test coverage baseline** - Run test suite and record count before starting
2. **Performance benchmark** - Measure current streaming performance to compare after
3. **Approval flow documentation** - Document current step-by-step before Phase 3
4. **State field audit** - Complete mapping of all state fields before Phase 3

## Sources (Aggregated)

### Official Microsoft Documentation
- [ASP.NET Core Blazor state management](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/?view=aspnetcore-10.0)
- [ASP.NET Core Razor component rendering](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/rendering?view=aspnetcore-10.0)
- [Blazor component disposal](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/component-disposal)
- [Blazor synchronization context](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/synchronization-context)
- [SignalR with Blazor](https://learn.microsoft.com/en-us/aspnet/core/blazor/tutorials/signalr-blazor?view=aspnetcore-10.0)

### Library Documentation
- [Fluxor GitHub Repository](https://github.com/mrpmorris/Fluxor)
- [TimeWarp.State GitHub](https://github.com/TimeWarpEngineering/timewarp-state)

### Community Resources (Validated Patterns)
- [Code Maze: Fluxor for State Management](https://code-maze.com/fluxor-for-state-management-in-blazor/)
- [Infragistics: Blazor State Management Best Practices](https://www.infragistics.com/blogs/blazor-state-management/)
- [Blazor University: InvokeAsync Thread Safety](https://blazor-university.com/components/multi-threaded-rendering/invokeasync/)
- [DEV Community: Fluxor + SignalR](https://dev.to/mr_eking/advanced-blazor-state-management-using-fluxor-part-7-client-to-client-comms-with-signalr-4p9)
