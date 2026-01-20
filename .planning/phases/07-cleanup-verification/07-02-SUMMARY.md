---
phase: 07
plan: 02
subsystem: webchat-client
tags: [streaming, refactoring, cleanup, store-pattern]
dependency-graph:
  requires:
    - phase-06 (Clean Architecture Flux stores)
    - plan-01 (ChatStateManager cleanup)
  provides:
    - StreamingCoordinator removed
    - BufferRebuildUtility for pure buffer rebuild functions
    - IStreamingService/StreamingService for streaming operations
  affects:
    - plan-03 (final verification)
tech-stack:
  patterns:
    - Static utility classes for pure functions
    - Store-based dispatch instead of callbacks
key-files:
  created:
    - WebChat.Client/Services/Streaming/BufferRebuildUtility.cs
    - WebChat.Client/Contracts/IStreamingService.cs
    - WebChat.Client/Services/Streaming/StreamingService.cs
    - Tests/Unit/WebChat/Client/BufferRebuildUtilityTests.cs
    - Tests/Unit/WebChat/Client/StreamingServiceTests.cs
    - Tests/Integration/WebChat/Client/StreamingServiceIntegrationTests.cs
  modified:
    - WebChat.Client/Program.cs
    - WebChat.Client/State/Effects/SendMessageEffect.cs
    - WebChat.Client/Services/Streaming/StreamResumeService.cs
    - Tests/Unit/WebChat/Client/StreamResumeServiceTests.cs
    - Tests/Unit/WebChat/Client/ChatNotificationHandlerTests.cs
    - Tests/Integration/WebChat/Client/NotificationHandlerIntegrationTests.cs
    - Tests/Integration/WebChat/Client/StreamResumeServiceIntegrationTests.cs
  deleted:
    - WebChat.Client/Services/Streaming/StreamingCoordinator.cs
    - WebChat.Client/Contracts/IStreamingCoordinator.cs
    - Tests/Unit/WebChat/Client/StreamingCoordinatorTests.cs
    - Tests/Integration/WebChat/Client/StreamingCoordinatorIntegrationTests.cs
decisions:
  - id: static-utility-for-buffer-rebuild
    decision: Extract RebuildFromBuffer, StripKnownContent, AccumulateChunk to static BufferRebuildUtility
    rationale: Pure functions with no dependencies, can be called directly without instantiation
  - id: remove-render-callback
    decision: Remove Func<Task> onRender callback from streaming methods
    rationale: Components subscribe to stores directly, no need for callback-based rendering
metrics:
  duration: 15m
  completed: 2026-01-20
---

# Phase 7 Plan 2: Delete StreamingCoordinator and Migrate to StreamingService Summary

Deleted StreamingCoordinator, extracted pure functions to BufferRebuildUtility, and created IStreamingService for streaming operations.

## Key Changes

### Created BufferRebuildUtility
- Static class with pure functions for buffer rebuild operations
- `RebuildFromBuffer()` - reconstructs messages from server buffer
- `StripKnownContent()` - removes duplicate content already in history
- `AccumulateChunk()` (internal) - accumulates stream chunks into message

### Created IStreamingService/StreamingService
- Interface for streaming operations: `StreamResponseAsync`, `ResumeStreamResponseAsync`
- Implementation uses store-based dispatch instead of callbacks
- No render callback needed - components subscribe to stores directly
- Uses `BufferRebuildUtility.AccumulateChunk()` for chunk processing

### Deleted StreamingCoordinator
- Removed `StreamingCoordinator.cs` and `IStreamingCoordinator.cs`
- All streaming logic moved to `StreamingService`
- Buffer rebuild logic extracted to `BufferRebuildUtility`

### Updated Consumers
- **SendMessageEffect**: Uses `IStreamingService.StreamResponseAsync()` without callback
- **StreamResumeService**: Uses `IStreamingService` and `BufferRebuildUtility` static methods
- **Program.cs**: Registers `IStreamingService` instead of `IStreamingCoordinator`

### Migrated Tests
- Created `BufferRebuildUtilityTests` with pure function tests
- Created `StreamingServiceTests` with streaming operation tests
- Created `StreamingServiceIntegrationTests` for integration tests
- Updated all tests that previously used `StreamingCoordinator`
- Removed `SetRenderCallback` tests (method no longer exists)

## Deviations from Plan

None - plan executed exactly as written.

## Verification Results

- Build entire solution: SUCCESS (0 errors, 0 warnings)
- All 664 tests pass
- No StreamingCoordinator references in WebChat.Client
- No StreamingCoordinator references in Tests

## Commits

| Hash    | Type     | Description                                        |
|---------|----------|----------------------------------------------------|
| d49a36e | feat     | Add BufferRebuildUtility and IStreamingService     |
| 1f3847c | refactor | Delete StreamingCoordinator and migrate consumers  |
| e932b59 | test     | Migrate tests to use StreamingService              |

## Next Phase Readiness

Plan 07-03 can proceed with final verification:
- All old state management classes removed
- All consumers use Flux stores
- Test suite passing
