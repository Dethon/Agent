# Project State: Agent WebChat

## Project Reference

See: `.planning/PROJECT.md` (updated 2026-01-21)

**Core Value:** People can have personalized conversations with agents in shared topics
**Current Focus:** Milestone v1.1 Users in Web UI — Phase 8 (User Identity) COMPLETE

**Key Files:**
- `.planning/PROJECT.md` - Project definition
- `.planning/ROADMAP.md` - v1.1 roadmap (phases 8-10)
- `.planning/REQUIREMENTS.md` - v1.1 requirements with traceability
- `.planning/MILESTONES.md` - Milestone history
- `.planning/milestones/v1.0-ROADMAP.md` - Archived v1.0 roadmap
- `.planning/milestones/v1.0-REQUIREMENTS.md` - Archived v1.0 requirements

## Current Position

**Milestone:** v1.1 Users in Web UI
**Phase:** 8 of 3 (User Identity) - COMPLETE
**Plan:** 2 of 2 complete
**Status:** Phase complete
**Last activity:** 2026-01-21 — Completed 08-02-PLAN.md

**Progress:**
```
v1.1 Users in Web UI: [████████░░░░░░░░░░░░░░░░] 33% (2/6 plans)

Phase 8: User Identity       [████████] 2/2 plans complete
Phase 9: Message Attribution [░░░░░░░░] 0/? plans
Phase 10: Backend Integration [░░░░░░░░] 0/? plans
```

## Phase Summary

| Phase | Goal | Requirements | Status |
|-------|------|--------------|--------|
| 8 | Users can establish their identity | USER-01, USER-02, USER-03 | COMPLETE |
| 9 | Users can see who sent each message | MSG-01, MSG-02, MSG-03 | Pending |
| 10 | Backend knows who is sending messages | BACK-01, BACK-02, BACK-03 | Pending |

## Phase 8 Success Criteria

1. [x] User sees avatar picker when no username is set (shows "?" placeholder)
2. [x] User can select a username from predefined list (dropdown menu)
3. [x] After selecting user, picker shows their avatar
4. [x] Refreshing the page preserves the username (localStorage persistence)
5. [x] Avatar is automatically determined based on username (users.json mapping)

## Accumulated Context

**From v1.0:**
- State management uses Store<T> with BehaviorSubject
- Components extend StoreSubscriberComponent for subscriptions
- HubEventDispatcher routes SignalR events to store actions
- 50ms render throttling prevents UI freezes
- Dispatcher pattern: IDispatcher for components, Dispatcher for stores

**For v1.1:**
- WebChat uses Blazor WebAssembly with SignalR
- Topics are already shared/broadcast (existing functionality)
- Avatars are hardcoded lookup, not user-selected
- Username stored in localStorage (client-side only)

**From 08-01:**
- UserConfig record type with Id, Username, AvatarUrl properties
- UserIdentityStore follows TopicsStore pattern (Dispatcher.RegisterHandler)
- Three predefined users (Alice, Bob, Charlie) in users.json
- Avatar images stored in wwwroot/avatars/

**From 08-02:**
- UserIdentityEffect loads users.json on Initialize action
- UserIdentityPicker component with circular avatar button and dropdown
- Selection persisted to localStorage with key "selectedUserId"
- Component integrated into MainLayout header

## Decisions Log

| Phase-Plan | Decision | Rationale |
|------------|----------|-----------|
| 08-01 | UserConfig uses record type | Immutability and value equality |
| 08-01 | Three predefined users | Sufficient for initial testing |
| 08-02 | Effect loads on Initialize action | Same trigger as InitializationEffect |
| 08-02 | localStorage key "selectedUserId" | Simple, descriptive key |

## Session Continuity

**Last session:** 2026-01-21T02:24:00Z
**Stopped at:** Completed 08-02-PLAN.md
**Resume file:** None

## Next Steps

1. Plan Phase 9 - Message Attribution
2. Execute Phase 9 plans

---
*State initialized: 2026-01-19*
*Last updated: 2026-01-21 — Completed 08-02-PLAN.md (Phase 8 complete)*
