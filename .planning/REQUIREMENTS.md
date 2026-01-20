# Requirements: WebChat Stack Refactoring

**Defined:** 2026-01-19
**Core Value:** State flows in one direction - down from stores, up via events

## v1 Requirements

Requirements for the refactoring. Each maps to roadmap phases.

### State Foundation

- [ ] **STATE-01**: Consolidated state stores replace scattered state across ChatStateManager, StreamingCoordinator, StreamResumeService
- [ ] **STATE-02**: State represented as immutable C# record types with structural equality
- [ ] **STATE-03**: Action -> Reducer -> State -> UI unidirectional flow pattern implemented
- [ ] **STATE-04**: State selectors with memoization for derived state
- [ ] **STATE-05**: StoreSubscriberComponent base class for components that subscribe to state

### State Slices

- [ ] **SLICE-01**: TopicsState slice for topic list and selection (changes rarely)
- [ ] **SLICE-02**: MessagesState slice for message history per topic
- [ ] **SLICE-03**: StreamingState slice for active streaming with throttled updates
- [ ] **SLICE-04**: ConnectionState slice for SignalR connection status
- [ ] **SLICE-05**: ApprovalState slice for tool approval modal

### Streaming Performance

- [ ] **PERF-01**: Selective re-rendering - streaming state notifications separate from topic state
- [ ] **PERF-02**: Preserved 50ms throttled rendering to prevent UI freeze during streaming
- [ ] **PERF-03**: Thread-safe state mutations - all state access wrapped in InvokeAsync

### SignalR Integration

- [ ] **HUB-01**: HubEventDispatcher routes SignalR events to store actions
- [ ] **HUB-02**: Reconnection preserves streaming state and resumes properly
- [ ] **HUB-03**: Event subscription lifecycle properly managed (dispose on component disposal)

### Component Architecture

- [ ] **COMP-01**: Components dispatch actions only, render from store state
- [ ] **COMP-02**: ChatContainer.razor broken into smaller focused components
- [ ] **COMP-03**: Components stay under 100 lines (render-focused, no business logic)

### Clean Architecture

- [ ] **ARCH-01**: INotifier implementation moved from Agent/Hubs to Infrastructure
- [ ] **ARCH-02**: State stores registered in proper layer (Infrastructure or WebChat.Client)
- [ ] **ARCH-03**: No layer violations in refactored code

### Cleanup

- [ ] **CLEAN-01**: ChatStateManager deleted (replaced by stores)
- [ ] **CLEAN-02**: StreamingCoordinator deleted (logic moved to StreamingState + reducers)
- [ ] **CLEAN-03**: All existing tests pass with equivalent or better coverage

## v2 Requirements

Deferred to future. Tracked but not in current roadmap.

### Developer Experience

- **DX-01**: Redux DevTools integration for state inspection
- **DX-02**: Time-travel debugging support
- **DX-03**: Performance benchmarking dashboard

### Advanced Patterns

- **ADV-01**: Optimistic updates for message sending
- **ADV-02**: State validation middleware
- **ADV-03**: State persistence to localStorage

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Fluxor library | Adds 300KB bundle size, overkill for chat domain |
| New WebChat features | This is refactoring, not feature work |
| Telegram/CLI changes | Different codepaths, not affected by this work |
| MCP server changes | Orthogonal concern |
| API signature changes | Must maintain backwards compatibility |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| STATE-01 | Phase 1 | Complete |
| STATE-02 | Phase 1 | Complete |
| STATE-03 | Phase 1 | Complete |
| STATE-04 | Phase 1 | Complete |
| STATE-05 | Phase 1 | Complete |
| SLICE-01 | Phase 2 | Complete |
| SLICE-02 | Phase 2 | Complete |
| SLICE-03 | Phase 2 | Complete |
| SLICE-04 | Phase 2 | Complete |
| SLICE-05 | Phase 2 | Complete |
| PERF-01 | Phase 3 | Complete |
| PERF-02 | Phase 3 | Complete |
| PERF-03 | Phase 3 | Complete |
| HUB-01 | Phase 4 | Complete |
| HUB-02 | Phase 4 | Complete |
| HUB-03 | Phase 4 | Complete |
| COMP-01 | Phase 5 | Complete |
| COMP-02 | Phase 5 | Complete |
| COMP-03 | Phase 5 | Complete |
| ARCH-01 | Phase 6 | Pending |
| ARCH-02 | Phase 6 | Pending |
| ARCH-03 | Phase 6 | Pending |
| CLEAN-01 | Phase 7 | Pending |
| CLEAN-02 | Phase 7 | Pending |
| CLEAN-03 | Phase 7 | Pending |

**Coverage:**
- v1 requirements: 25 total
- Mapped to phases: 25
- Unmapped: 0

---
*Requirements defined: 2026-01-19*
*Last updated: 2026-01-20 (Phase 5 complete)*
