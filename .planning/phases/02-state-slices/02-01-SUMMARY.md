---
phase: 02-state-slices
plan: 01
subsystem: ui
tags: [blazor, state-management, redux-pattern, immutable, observable]

# Dependency graph
requires:
  - phase: 01-state-foundation
    provides: Store<TState>, Dispatcher, IAction, Selector
provides:
  - TopicsState slice with state, actions, reducers, store, selectors
  - MessagesState slice with state, actions, reducers, store, selectors
  - Unit tests verifying store behavior
affects: [02-02, 02-03, component-architecture, signalr-integration]

# Tech tracking
tech-stack:
  added: []
  patterns: [feature-store-wrapper, switch-expression-reducers, immutable-dictionary-pattern, immutable-set-pattern, parameterized-selectors]

key-files:
  created:
    - WebChat.Client/State/Topics/TopicsState.cs
    - WebChat.Client/State/Topics/TopicsActions.cs
    - WebChat.Client/State/Topics/TopicsReducers.cs
    - WebChat.Client/State/Topics/TopicsStore.cs
    - WebChat.Client/State/Topics/TopicsSelectors.cs
    - WebChat.Client/State/Messages/MessagesState.cs
    - WebChat.Client/State/Messages/MessagesActions.cs
    - WebChat.Client/State/Messages/MessagesReducers.cs
    - WebChat.Client/State/Messages/MessagesStore.cs
    - WebChat.Client/State/Messages/MessagesSelectors.cs
    - Tests/Unit/WebChat.Client/State/TopicsStoreTests.cs
    - Tests/Unit/WebChat.Client/State/MessagesStoreTests.cs
  modified: []

key-decisions:
  - "StoredTopic kept as mutable class with reference comparison - no conversion needed since reducer creates new lists"
  - "SelectedTopicId stores ID only, not full topic - derived via selector"
  - "Messages normalized by TopicId in dictionary for O(1) topic switching"
  - "LoadedTopics set tracks which topics have been loaded vs empty"
  - "Error auto-clears on successful actions per CONTEXT.md"
  - "RemoveTopic clears selection if removed topic was selected"
  - "Parameterized selector factories for per-topic queries"

patterns-established:
  - "Feature store wraps Store<TState> and registers handlers in constructor"
  - "Reducers use switch expression with pattern matching on action types"
  - "Dictionary mutation pattern: new Dictionary<K,V>(existing) { [key] = value }"
  - "Set mutation pattern: new HashSet<T>(existing) { item } or Where filter"
  - "Selector factories return new Selector instances for parameterized queries"

# Metrics
duration: 4min
completed: 2026-01-20
---

# Phase 2 Plan 1: Topics and Messages State Summary

**TopicsState and MessagesState slices with immutable reducers, action handlers, and selector factories for topic management and per-topic message storage**

## Performance

- **Duration:** 4 min
- **Started:** 2026-01-19T23:56:03Z
- **Completed:** 2026-01-20T00:00:21Z
- **Tasks:** 3
- **Files created:** 12

## Accomplishments
- TopicsState slice with 9 action handlers managing topics, selection, agents, and error states
- MessagesState slice with 6 action handlers using dictionary normalization for O(1) topic switching
- 29 unit tests verifying store behavior including immutability and observable emissions

## Task Commits

Each task was committed atomically:

1. **Task 1: Create TopicsState slice** - `c6f7b7a` (feat)
2. **Task 2: Create MessagesState slice** - `dcc6bc5` (feat)
3. **Task 3: Add unit tests for TopicsStore and MessagesStore** - `e063522` (test)

## Files Created

**TopicsState slice (5 files):**
- `WebChat.Client/State/Topics/TopicsState.cs` - Immutable state record with Topics, SelectedTopicId, Agents, SelectedAgentId, IsLoading, Error
- `WebChat.Client/State/Topics/TopicsActions.cs` - 9 action records: LoadTopics, TopicsLoaded, SelectTopic, AddTopic, UpdateTopic, RemoveTopic, SetAgents, SelectAgent, TopicsError
- `WebChat.Client/State/Topics/TopicsReducers.cs` - Pure reducers with switch expression, immutable list mutations
- `WebChat.Client/State/Topics/TopicsStore.cs` - Store wrapper registering all action handlers
- `WebChat.Client/State/Topics/TopicsSelectors.cs` - SelectedTopic, TopicsForSelectedAgent, TopicsForAgent factory

**MessagesState slice (5 files):**
- `WebChat.Client/State/Messages/MessagesState.cs` - Immutable state with MessagesByTopic dictionary and LoadedTopics set
- `WebChat.Client/State/Messages/MessagesActions.cs` - 6 action records: LoadMessages, MessagesLoaded, AddMessage, UpdateMessage, RemoveLastMessage, ClearMessages
- `WebChat.Client/State/Messages/MessagesReducers.cs` - Pure reducers with immutable dictionary/set mutations
- `WebChat.Client/State/Messages/MessagesStore.cs` - Store wrapper registering all action handlers
- `WebChat.Client/State/Messages/MessagesSelectors.cs` - MessagesForTopic, HasMessagesForTopic, MessageCount factories

**Tests (2 files):**
- `Tests/Unit/WebChat.Client/State/TopicsStoreTests.cs` - 15 tests for topic store behavior
- `Tests/Unit/WebChat.Client/State/MessagesStoreTests.cs` - 14 tests for message store behavior

## Decisions Made
- **StoredTopic unchanged:** Kept as mutable class since reducers create new lists and reference equality handles change detection
- **ID-only selection:** SelectedTopicId stores string, SelectedTopic derived via selector - avoids stale reference issues
- **Dictionary normalization:** MessagesByTopic keyed by TopicId enables instant topic switching without filtering
- **LoadedTopics tracking:** Distinguishes "no messages" from "not yet loaded" for lazy loading
- **Parameterized selector factories:** MessagesForTopic(topicId) returns new selector per topic - supports multiple concurrent subscriptions

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- TopicsState and MessagesState ready for component integration
- Foundation patterns established for remaining slices (Streaming, Connection, Approval)
- Plan 02-02 can implement StreamingState and ConnectionState following same patterns
- Plan 02-03 can implement ApprovalState

---
*Phase: 02-state-slices*
*Plan: 01*
*Completed: 2026-01-20*
