---
phase: 03-streaming-performance
plan: 01
subsystem: ui
tags: [rx-net, sample-operator, throttling, blazor, observables]

requires:
  - phase: 02-state-slices
    provides: StreamingStore with StateObservable
provides:
  - RenderCoordinator service with 50ms Sample-based throttling
  - StreamingSelectors for topic-scoped content selection
  - SubscribeWithInvoke method for pre-throttled observables
  - ClearSubscriptions method for re-subscription scenarios
affects: [03-02-streaming-visual-feedback, 04-signalr-integration, 05-component-architecture]

tech-stack:
  added: []
  patterns:
    - "Sample operator for periodic render ticks (NOT Throttle)"
    - "Selector factories returning Func<TState, TSelected>"
    - "Centralized throttling in RenderCoordinator"

key-files:
  created:
    - WebChat.Client/State/Streaming/StreamingSelectors.cs
    - WebChat.Client/State/RenderCoordinator.cs
    - Tests/Unit/WebChat.Client/State/RenderCoordinatorTests.cs
  modified:
    - WebChat.Client/State/StoreSubscriberComponent.cs
    - WebChat.Client/Program.cs

key-decisions:
  - "Sample operator instead of Throttle (Rx.NET Throttle is debounce)"
  - "Single throttling point in RenderCoordinator (consumers don't throttle)"
  - "SubscribeWithInvoke only does InvokeAsync marshaling (no throttling)"

patterns-established:
  - "RenderCoordinator.CreateStreamingObservable(topicId) for throttled subscriptions"
  - "StreamingSelectors.SelectStreamingContent(topicId) for topic-scoped selection"
  - "ClearSubscriptions() before re-subscribing on parameter changes"

duration: 5min
completed: 2026-01-20
---

# Phase 03 Plan 01: Render Coordination Summary

**RenderCoordinator with 50ms Sample-based throttling, StreamingSelectors for topic-scoped content, and SubscribeWithInvoke for UI thread marshaling**

## Performance

- **Duration:** 5 min
- **Started:** 2026-01-20T10:09:38Z
- **Completed:** 2026-01-20T10:15:01Z
- **Tasks:** 3
- **Files modified:** 5

## Accomplishments

- RenderCoordinator service providing 50ms-sampled observables for streaming content
- StreamingSelectors enabling topic-specific subscriptions without cross-topic noise
- SubscribeWithInvoke method for consuming pre-throttled observables
- ClearSubscriptions method for handling parameter changes
- 9 unit tests verifying Sample behavior and throttling

## Task Commits

Each task was committed atomically:

1. **Task 1: Create StreamingSelectors for topic-scoped content selection** - `6b5834f` (feat)
2. **Task 2: Create RenderCoordinator service with 50ms Sample operator** - `718ec63` (feat)
3. **Task 3: Add SubscribeWithInvoke, ClearSubscriptions, and tests** - `384c1c2` (feat)

## Files Created/Modified

- `WebChat.Client/State/Streaming/StreamingSelectors.cs` - Selector factories for topic-scoped selection
- `WebChat.Client/State/RenderCoordinator.cs` - Centralized 50ms throttling service
- `WebChat.Client/State/StoreSubscriberComponent.cs` - Added SubscribeWithInvoke and ClearSubscriptions
- `WebChat.Client/Program.cs` - Registered RenderCoordinator in DI
- `Tests/Unit/WebChat.Client/State/RenderCoordinatorTests.cs` - 9 unit tests for throttling behavior

## Decisions Made

- **Sample operator instead of Throttle:** Rx.NET Throttle is actually debounce (resets on each event). Sample emits the latest value at fixed intervals, which is exactly what we need for periodic render ticks.
- **Centralized throttling in RenderCoordinator:** Single point where Sample is applied. Consumers (SubscribeWithInvoke) do NOT apply additional throttling.
- **SubscribeWithInvoke naming:** Name clarifies its only responsibility is InvokeAsync marshaling, not throttling.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

- **Timing-sensitive tests:** Initial tests used 60ms wait times which caused intermittent failures due to Sample operator timing. Fixed by increasing to 120ms for reliable capture across multiple sample intervals.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- RenderCoordinator ready for streaming components in Plan 02
- SubscribeWithInvoke available for any component needing throttled streaming
- ClearSubscriptions supports topic change scenarios
- All 9 tests passing reliably

---
*Phase: 03-streaming-performance*
*Completed: 2026-01-20*
