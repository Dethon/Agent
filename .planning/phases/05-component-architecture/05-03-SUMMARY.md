---
phase: 05-component-architecture
plan: 03
subsystem: webchat-components
tags: [blazor, store-subscription, topic-list, component-migration]

dependency-graph:
  requires: [05-01]
  provides: [topic-list-migration, store-based-topic-sidebar]
  affects: [05-05, 05-06]

tech-stack:
  patterns: [store-subscriber-component, multi-store-subscription, computed-state]

key-files:
  created: []
  modified:
    - WebChat.Client/Components/TopicList.razor
    - WebChat.Client/State/Topics/TopicsActions.cs
    - WebChat.Client/State/Topics/TopicsReducers.cs
    - WebChat.Client/State/Topics/TopicsStore.cs
    - WebChat.Client/_Imports.razor

decisions:
  - id: "multi-store-subscription"
    summary: "TopicList subscribes to TopicsStore, StreamingStore, and MessagesStore"
  - id: "computed-unread-counts"
    summary: "Unread counts computed from MessagesStore combined with TopicsStore"
  - id: "create-new-topic-action"
    summary: "CreateNewTopic action clears SelectedTopicId for new conversation"

metrics:
  completed: "2026-01-20"
  duration: "~4 minutes"
---

# Phase 05 Plan 03: TopicList Component Summary

TopicList migrated to store-based pattern with multiple subscriptions to TopicsStore, StreamingStore, and MessagesStore.

## What Was Done

### Task 1: Add CreateNewTopic Action
- Added `CreateNewTopic` action to `TopicsActions.cs`
- Added reducer case that sets `SelectedTopicId` to null
- Registered handler in `TopicsStore`

### Task 2: Migrate TopicList to Store Subscriptions
- Inherited from `StoreSubscriberComponent` for subscription lifecycle management
- Removed all `[Parameter]` properties (12 parameters removed)
- Added private fields for topics, selection, agents, streaming topics
- Subscribed to `TopicsStore` for topics, selectedTopicId, agents, selectedAgentId
- Subscribed to `StreamingStore` for streaming topic indicators
- Updated event handlers to dispatch actions: `SelectTopic`, `RemoveTopic`, `SelectAgent`, `CreateNewTopic`

### Task 3: Add Unread Counts Subscription
- Injected `MessagesStore` for message data access
- Added subscription that computes unread counts from `MessagesStore` combined with `TopicsStore` state
- `ComputeUnreadCounts` method compares assistant message count to last read count per topic

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed missing State namespace imports**
- **Found during:** Task 1 verification
- **Issue:** MessageList.razor (from previous plan) failed to compile due to missing @using directives
- **Fix:** Added State namespace imports to `_Imports.razor`: `WebChat.Client.State`, `WebChat.Client.State.Topics`, `WebChat.Client.State.Messages`, `WebChat.Client.State.Streaming`, `WebChat.Client.State.Connection`, `WebChat.Client.State.Approval`
- **Files modified:** `WebChat.Client/_Imports.razor`
- **Commit:** 5cc097a

**2. [Rule 3 - Blocking] Fixed StreamingState property reference**
- **Found during:** Task 1 verification (after imports fix)
- **Issue:** MessageList.razor referenced `state.ActiveStreams` but property is `StreamingByTopic`
- **Fix:** Updated references to use correct property name
- **Files modified:** `WebChat.Client/Components/Chat/MessageList.razor`
- **Commit:** 5cc097a

## Commits

| Commit | Type | Description |
|--------|------|-------------|
| 5cc097a | fix | Fix blocking build errors from previous plan |
| 9daafb0 | feat | Add CreateNewTopic action |
| bca8fb3 | feat | Migrate TopicList to store subscriptions |
| 6493ce1 | feat | Add unread counts subscription from MessagesStore |

## Component Migration Summary

**Before:**
- 12 `[Parameter]` properties (Topics, SelectedTopic, Agents, SelectedAgentId, etc.)
- 8 `EventCallback` parameters for parent communication
- Parent passed all data and handled all events

**After:**
- 0 `[Parameter]` properties
- Subscribes directly to 3 stores (TopicsStore, StreamingStore, MessagesStore)
- Dispatches 4 actions (SelectTopic, RemoveTopic, SelectAgent, CreateNewTopic)
- Self-contained component with automatic subscription cleanup

## Verification

- [x] `dotnet build WebChat.Client` compiles without errors
- [x] TopicList.razor inherits `StoreSubscriberComponent`
- [x] TopicList subscribes to TopicsStore, StreamingStore, MessagesStore
- [x] TopicList dispatches SelectTopic, RemoveTopic, SelectAgent, CreateNewTopic
- [x] No `[Parameter]` attributes remain
- [x] TopicsActions.cs contains CreateNewTopic action

## Next Phase Readiness

**Ready for 05-04 (Effects Pattern):** TopicList now dispatches actions that effects can listen to.

**Dependencies satisfied:**
- CreateNewTopic action available for topic creation flow
- SelectTopic action dispatched for topic selection handling
- RemoveTopic action dispatched for topic deletion handling
