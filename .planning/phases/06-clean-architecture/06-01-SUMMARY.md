---
phase: 06-clean-architecture
plan: 01
subsystem: infra
tags: [signalr, di, clean-architecture, adapter-pattern]

# Dependency graph
requires:
  - phase: 05-component-architecture
    provides: Store pattern foundation for WebChat
provides:
  - IHubNotificationSender interface in Domain/Contracts
  - HubNotifier implementation in Infrastructure/Clients/Messaging
  - HubNotificationAdapter in Agent/Hubs (SignalR wrapper)
  - Clean layer separation for notifications
affects: [phase-07-cleanup, future-notifier-extensions]

# Tech tracking
tech-stack:
  added: []
  patterns: [adapter-pattern-for-framework-dependencies]

key-files:
  created:
    - Domain/Contracts/IHubNotificationSender.cs
    - Infrastructure/Clients/Messaging/HubNotifier.cs
    - Agent/Hubs/HubNotificationAdapter.cs
  modified:
    - Agent/Modules/InjectorModule.cs
    - Tests/Integration/Fixtures/WebChatServerFixture.cs

key-decisions:
  - "Adapter pattern for SignalR: IHubNotificationSender abstracts hub context"
  - "Single method interface: SendAsync(methodName, notification, ct) covers all notification types"

patterns-established:
  - "Framework adapter pattern: Wrap framework-specific types in adapters registered at composition root"

# Metrics
duration: 17min
completed: 2026-01-20
---

# Phase 6 Plan 1: INotifier Migration Summary

**INotifier implementation moved to Infrastructure layer using adapter pattern to abstract SignalR hub context**

## Performance

- **Duration:** 17 min
- **Started:** 2026-01-20T14:23:43Z
- **Completed:** 2026-01-20T14:40:24Z
- **Tasks:** 3
- **Files modified:** 5 (1 deleted)

## Accomplishments

- INotifier implementation (HubNotifier) now resides in Infrastructure/Clients/Messaging
- SignalR-specific code isolated to HubNotificationAdapter in Agent/Hubs
- Clean dependency flow: Domain <- Infrastructure <- Agent maintained
- All 729 tests pass (2 pre-existing failures unrelated to changes)

## Task Commits

Each task was committed atomically:

1. **Task 1: Create IHubNotificationSender interface and HubNotifier implementation** - `140b9d3` (feat)
2. **Task 2: Create HubNotificationAdapter and update DI registration** - `776eec3` (feat)
3. **Task 3: Verify layer compliance and run tests** - No commit (verification only)

## Files Created/Modified

- `Domain/Contracts/IHubNotificationSender.cs` - Interface abstracting hub notification sending
- `Infrastructure/Clients/Messaging/HubNotifier.cs` - INotifier implementation using IHubNotificationSender
- `Agent/Hubs/HubNotificationAdapter.cs` - IHubNotificationSender implementation wrapping IHubContext<ChatHub>
- `Agent/Hubs/Notifier.cs` - DELETED (replaced by HubNotifier)
- `Agent/Modules/InjectorModule.cs` - Updated DI registration for adapter pattern
- `Tests/Integration/Fixtures/WebChatServerFixture.cs` - Updated test fixture for new registration

## Decisions Made

- **Adapter pattern for SignalR:** Created IHubNotificationSender as a simple abstraction over SignalR's SendAsync. This allows Infrastructure to implement INotifier without knowing about IHubContext.
- **Single generic method:** IHubNotificationSender has one method `SendAsync(methodName, notification, ct)` rather than mirroring all notification methods. This keeps the adapter thin and focused.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed test fixture compilation error**
- **Found during:** Task 2 (DI registration update)
- **Issue:** WebChatServerFixture.cs referenced deleted Notifier class
- **Fix:** Updated fixture to use HubNotificationAdapter and HubNotifier
- **Files modified:** Tests/Integration/Fixtures/WebChatServerFixture.cs
- **Verification:** `dotnet build` succeeds
- **Committed in:** 776eec3 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (blocking)
**Impact on plan:** Test fixture update was necessary for compilation. No scope creep.

## Issues Encountered

- **File locking during test runs:** Windows testhost processes retained file locks causing rebuild failures. Mitigated by using `--no-build` flag after initial build.
- **Pre-existing test failures:** 2 tests in StreamResumeServiceTests fail (unrelated to this migration - they test WebChat.Client stream resumption logic)

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- INotifier migration complete
- ARCH-01 requirement satisfied: INotifier implementation in Infrastructure layer
- Clean architecture layer compliance verified
- Ready for Plan 02 (store registration) or Plan 03 (layer violation audit)

---
*Phase: 06-clean-architecture*
*Completed: 2026-01-20*
