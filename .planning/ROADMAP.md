# Roadmap: WebChat Stack Refactoring

**Created:** 2026-01-19
**Depth:** Comprehensive
**Phases:** 7
**Requirements:** 25 v1 mapped

## Overview

This roadmap transforms the WebChat stack from scattered bidirectional state management to a unidirectional Flux-inspired pattern. Phases are ordered by dependency: foundation infrastructure first, then state slices, then migration of existing code, then component simplification, and finally cleanup. Each phase delivers a coherent, verifiable capability that can be tested before proceeding.

---

## Phase 1: State Foundation

**Goal:** Core state management infrastructure exists for unidirectional data flow.

**Dependencies:** None (foundation phase)

**Plans:** 3 plans

Plans:
- [x] 01-01-PLAN.md - Core infrastructure (System.Reactive, IAction, Store)
- [x] 01-02-PLAN.md - Dispatch and subscription (IDispatcher, Dispatcher, StoreSubscriberComponent)
- [x] 01-03-PLAN.md - Memoized selectors (Selector<TState, TResult>)

**Requirements:**
- STATE-01: Consolidated state stores replace scattered state (infrastructure in Phase 1; concrete stores in Phase 2)
- STATE-02: State represented as immutable C# record types
- STATE-03: Action -> Reducer -> State -> UI unidirectional flow pattern
- STATE-04: State selectors with memoization for derived state
- STATE-05: StoreSubscriberComponent base class for components

**Success Criteria:**
1. Developer can create an action record, dispatch it to a store, and observe state change via subscription
2. State is immutable - mutations create new records via `with` keyword
3. Derived state (selectors) returns cached value when underlying state unchanged
4. StoreSubscriberComponent automatically subscribes/unsubscribes on lifecycle events
5. Existing functionality continues working (stores are additive, not replacing yet)

**Note on STATE-01:** Phase 1 creates the infrastructure (Store<TState>, Dispatcher, etc.) that enables consolidated stores. The actual consolidated feature stores (TopicsStore, MessagesStore, etc.) are created in Phase 2 when specific state slices are implemented.

---

## Phase 2: State Slices

**Goal:** Feature-specific state slices exist with clear ownership boundaries.

**Dependencies:** Phase 1 (State Foundation)

**Plans:** 3 plans

Plans:
- [x] 02-01-PLAN.md - Topics and Messages state slices (TopicsStore, MessagesStore)
- [x] 02-02-PLAN.md - Streaming and Connection state slices (StreamingStore, ConnectionStore)
- [x] 02-03-PLAN.md - Approval state slice and DI registration (ApprovalStore, Program.cs)

**Requirements:**
- SLICE-01: TopicsState slice for topic list and selection
- SLICE-02: MessagesState slice for message history per topic
- SLICE-03: StreamingState slice for active streaming with throttled updates
- SLICE-04: ConnectionState slice for SignalR connection status
- SLICE-05: ApprovalState slice for tool approval modal

**Success Criteria:**
1. Each state slice has its own store, actions, and reducers - no cross-slice direct access
2. TopicsState changes do not trigger MessagesState subscribers (selective notification)
3. StreamingState supports throttled notifications (50ms minimum interval)
4. ConnectionState reflects accurate SignalR status (Connected/Disconnected/Reconnecting)
5. ApprovalState can show/hide modal and track pending approval request

---

## Phase 3: Streaming Performance

**Goal:** Streaming updates render efficiently without UI freezes.

**Dependencies:** Phase 2 (State Slices)

**Requirements:**
- PERF-01: Selective re-rendering - streaming state notifications separate from topic state
- PERF-02: Preserved 50ms throttled rendering to prevent UI freeze
- PERF-03: Thread-safe state mutations - all state access wrapped in InvokeAsync

**Success Criteria:**
1. Streaming at 50+ updates/second does not freeze UI or cause visible lag
2. Topic sidebar does not re-render during active streaming
3. All store dispatch calls properly marshal to Blazor synchronization context
4. No race conditions observable during rapid state changes

---

## Phase 4: SignalR Integration

**Goal:** SignalR events flow through the unidirectional pattern via HubEventDispatcher.

**Dependencies:** Phase 3 (Streaming Performance)

**Requirements:**
- HUB-01: HubEventDispatcher routes SignalR events to store actions
- HUB-02: Reconnection preserves streaming state and resumes properly
- HUB-03: Event subscription lifecycle properly managed

**Success Criteria:**
1. All SignalR hub events (message received, streaming started, etc.) create actions, not direct state mutations
2. After reconnection, user sees streaming resume exactly where it left off
3. Navigating away and back does not create duplicate event subscriptions
4. No memory leaks from orphaned event handlers (verified via browser dev tools)

---

## Phase 5: Component Architecture

**Goal:** Components are thin render layers that dispatch actions and consume store state.

**Dependencies:** Phase 4 (SignalR Integration)

**Requirements:**
- COMP-01: Components dispatch actions only, render from store state
- COMP-02: ChatContainer.razor broken into smaller focused components
- COMP-03: Components stay under 100 lines

**Success Criteria:**
1. ChatContainer.razor reduced from 320 lines to under 100 lines
2. Message list, input, sidebar, header are separate components
3. Components contain no business logic - all decisions made in reducers or effects
4. Each component clearly shows: what store(s) it subscribes to, what actions it dispatches

---

## Phase 6: Clean Architecture Alignment

**Goal:** All WebChat code respects Domain -> Infrastructure -> Agent layering.

**Dependencies:** Phase 1 (can run partially in parallel with other phases, completed here)

**Requirements:**
- ARCH-01: INotifier implementation moved from Agent/Hubs to Infrastructure
- ARCH-02: State stores registered in proper layer
- ARCH-03: No layer violations in refactored code

**Success Criteria:**
1. Agent project has no state management implementations (only DI registration)
2. Infrastructure project contains INotifier implementation
3. WebChat.Client contains all client-side stores
4. No compilation warnings about layer violations
5. Dependency flow verified: Domain <- Infrastructure <- Agent

---

## Phase 7: Cleanup and Verification

**Goal:** Legacy code removed, full test coverage maintained, refactoring complete.

**Dependencies:** Phase 5 (Component Architecture), Phase 6 (Clean Architecture)

**Requirements:**
- CLEAN-01: ChatStateManager deleted (replaced by stores)
- CLEAN-02: StreamingCoordinator deleted (logic moved to StreamingState + reducers)
- CLEAN-03: All existing tests pass with equivalent or better coverage

**Success Criteria:**
1. ChatStateManager.cs file deleted from codebase
2. StreamingCoordinator.cs file deleted from codebase
3. No dead code remaining (classes, methods, or files that reference deleted types)
4. Test suite passes with same or better coverage percentage
5. All WebChat functionality verified: messaging, streaming, topics, reconnection, tool approval

---

## Progress

| Phase | Name | Requirements | Status |
|-------|------|--------------|--------|
| 1 | State Foundation | 5 | Complete |
| 2 | State Slices | 5 | Complete |
| 3 | Streaming Performance | 3 | Pending |
| 4 | SignalR Integration | 3 | Pending |
| 5 | Component Architecture | 3 | Pending |
| 6 | Clean Architecture | 3 | Pending |
| 7 | Cleanup and Verification | 3 | Pending |

**Total:** 25 requirements across 7 phases

---

## Requirement Coverage

| Category | Requirements | Phase(s) |
|----------|--------------|----------|
| State Foundation | STATE-01 to STATE-05 | Phase 1 |
| State Slices | SLICE-01 to SLICE-05 | Phase 2 |
| Streaming Performance | PERF-01 to PERF-03 | Phase 3 |
| SignalR Integration | HUB-01 to HUB-03 | Phase 4 |
| Component Architecture | COMP-01 to COMP-03 | Phase 5 |
| Clean Architecture | ARCH-01 to ARCH-03 | Phase 6 |
| Cleanup | CLEAN-01 to CLEAN-03 | Phase 7 |

**Coverage:** 25/25 requirements mapped (100%)

---
*Roadmap created: 2026-01-19*
*Last updated: 2026-01-20 (Phase 2 complete)*
