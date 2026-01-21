---
phase: 07
plan: 01
subsystem: webchat-client
tags: [flux, state-management, refactoring, cleanup]
dependency-graph:
  requires:
    - phase-06 (Clean Architecture Flux stores)
  provides:
    - ChatStateManager removed
    - All WebChat.Client services use Flux stores
    - Store-based test infrastructure
  affects:
    - plan-02 (removes dependency already migrated)
tech-stack:
  patterns:
    - IDispatcher for state mutations
    - Store subscriptions via IObservable
key-files:
  created: []
  modified:
    - WebChat.Client/Program.cs
    - WebChat.Client/Services/Handlers/ChatNotificationHandler.cs
    - WebChat.Client/Services/Streaming/StreamResumeService.cs
    - WebChat.Client/Services/Streaming/StreamingCoordinator.cs
    - WebChat.Client/Models/StoredTopic.cs
    - WebChat.Client/State/Streaming/StreamingReducers.cs
    - Tests/Unit/WebChat/Client/*.cs
    - Tests/Integration/WebChat/Client/*.cs
  deleted:
    - WebChat.Client/Services/State/ChatStateManager.cs
    - WebChat.Client/Contracts/IChatStateManager.cs
    - Tests/Unit/WebChat/Client/ChatStateManagerTests.cs
decisions:
  - id: migrate-streaming-coordinator-early
    decision: Migrated StreamingCoordinator to use stores in Plan 01 instead of Plan 02
    rationale: Required for compilation after deleting IChatStateManager
  - id: toolcalls-newline-separator
    decision: Use newline separator for ToolCalls accumulation in StreamingReducers
    rationale: Matches original ChatStateManager behavior
metrics:
  duration: 25m
  completed: 2026-01-20
---

# Phase 7 Plan 1: Delete ChatStateManager and Migrate Consumers Summary

Migrated all ChatStateManager consumers to use Flux stores and deleted the obsolete ChatStateManager class.

## Key Changes

### Deleted ChatStateManager
- Removed `WebChat.Client/Services/State/ChatStateManager.cs`
- Removed `WebChat.Client/Contracts/IChatStateManager.cs`
- Removed DI registration from `Program.cs`
- Deleted `ChatStateManagerTests.cs`

### Migrated ChatNotificationHandler
- Replaced `IChatStateManager` dependency with `IDispatcher` and store instances
- Topics: `_topicsStore.State.Topics` for reads, `AddTopic`/`UpdateTopic`/`RemoveTopic` actions for writes
- Streaming: `_streamingStore.State.StreamingTopics` for status checks, `StreamCompleted` action
- Approval: `_approvalStore.State.CurrentRequest` for reads, `ClearApproval` action
- Added `StoredTopic.ApplyMetadata()` method for updating topics from metadata

### Migrated StreamResumeService
- Replaced `IChatStateManager` dependency with `IDispatcher` and store instances
- Messages: `_messagesStore.State.MessagesByTopic` for reads, `MessagesLoaded`/`AddMessage` actions
- Streaming: `StartResuming`/`StopResuming`/`StreamStarted` actions for lifecycle
- Reads existing messages from store instead of calling `GetMessagesForTopic()`

### Migrated StreamingCoordinator (Originally planned for Plan 02)
- Migrated early as it was a blocking dependency for compilation
- Uses `IDispatcher` for all state mutations
- Dispatches `AddMessage`, `StreamChunk`, `StreamCompleted`, `ShowApproval` actions
- No longer takes `IChatStateManager` as constructor parameter

### Fixed StreamingReducers
- Added newline separator for ToolCalls accumulation to match original ChatStateManager behavior
- Content and Reasoning accumulate without separator (direct concatenation)

### Test Infrastructure Updates
- All unit tests now use `Dispatcher`, `TopicsStore`, `MessagesStore`, `StreamingStore`, `ApprovalStore`
- Tests create stores in constructor and dispose in `Dispose()`
- Tests dispatch actions instead of calling state manager methods
- Tests read state from store properties instead of state manager methods

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Migrated StreamingCoordinator early**
- **Found during:** Task 1
- **Issue:** StreamingCoordinator depended on IChatStateManager which was deleted
- **Fix:** Migrated StreamingCoordinator to use IDispatcher and stores
- **Files modified:** `WebChat.Client/Services/Streaming/StreamingCoordinator.cs`
- **Commit:** 7f38946

**2. [Rule 1 - Bug] Fixed ToolCalls separator in StreamingReducers**
- **Found during:** Task 3 (running tests)
- **Issue:** ToolCalls were concatenated without newline separator
- **Fix:** Added newline separator parameter to `AccumulateString` for ToolCalls
- **Files modified:** `WebChat.Client/State/Streaming/StreamingReducers.cs`
- **Commit:** 7941db8

## Verification Results

- All 668 unit tests pass
- Build succeeds with no warnings
- No remaining references to IChatStateManager

## Next Phase Readiness

Plan 02 is simplified since StreamingCoordinator was migrated in this plan. Plan 02 should focus on:
- Removing any remaining ChatStateManager migration code from Plan 02 scope
- Verifying remaining cleanup tasks
- Final documentation updates
