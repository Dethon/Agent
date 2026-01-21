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
**Phase:** 9 of 3 (Message Attribution) - COMPLETE
**Plan:** 2 of 2 complete
**Status:** Phase complete
**Last activity:** 2026-01-21 — Completed 09-02-PLAN.md

**Progress:**
```
v1.1 Users in Web UI: [████████████████░░░░░░░░] 67% (4/6 plans)

Phase 8: User Identity       [████████] 2/2 plans complete
Phase 9: Message Attribution [████████] 2/2 plans complete
Phase 10: Backend Integration [░░░░░░░░] 0/2 plans
```

## Phase Summary

| Phase | Goal | Requirements | Status |
|-------|------|--------------|--------|
| 8 | Users can establish their identity | USER-01, USER-02, USER-03 | COMPLETE |
| 9 | Users can see who sent each message | MSG-01, MSG-02, MSG-03 | COMPLETE |
| 10 | Backend knows who is sending messages | BACK-01, BACK-02, BACK-03 | Pending |

## Phase 8 Success Criteria

1. [x] User sees avatar picker when no username is set (shows "?" placeholder)
2. [x] User can select a username from predefined list (dropdown menu)
3. [x] After selecting user, picker shows their avatar
4. [x] Refreshing the page preserves the username (localStorage persistence)
5. [x] Avatar is automatically determined based on username (users.json mapping)

## Phase 9 Success Criteria

1. [x] Messages display sender's username (hover tooltip on message bubble)
2. [x] Messages display sender's avatar (28px circular, left of bubble)
3. [x] User's own messages visually distinguished (green gradient vs purple)
4. [x] Avatar grouping: first message in consecutive group shows avatar, others show placeholder
5. [x] Agent messages: full-width, no avatar column, no hover username

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

**From 09-01:**
- ChatMessageModel has SenderId, SenderUsername, SenderAvatarUrl properties (nullable)
- AvatarHelper provides deterministic color hashing and initials extraction
- AvatarImage component with image/fallback rendering (colored circle with initials)

**From 09-02:**
- ChatMessage renders avatar column (28px) for user messages with grouping support
- MessageList subscribes to UserIdentityStore and computes ShouldShowAvatar/IsOwnMessage
- Message grouping: avatar shows only on first message in consecutive group from same sender
- Own messages styled with green gradient (vs purple for other users)
- Username tooltip on message bubble hover (title attribute)

## Decisions Log

| Phase-Plan | Decision | Rationale |
|------------|----------|-----------|
| 08-01 | UserConfig uses record type | Immutability and value equality |
| 08-01 | Three predefined users | Sufficient for initial testing |
| 08-02 | Effect loads on Initialize action | Same trigger as InitializationEffect |
| 08-02 | localStorage key "selectedUserId" | Simple, descriptive key |
| 09-01 | Sender fields nullable in ChatMessageModel | Agent messages have null sender (Role="assistant" distinguishes them) |
| 09-01 | 8-color palette for avatar fallbacks | Visual variety while being memorable |
| 09-01 | Initials: 1 char single word, 2 chars multi | Balances clarity and compactness |
| 09-02 | ChatMessage parameters: ShowAvatar, IsOwnMessage | Simpler than passing full user context |
| 09-02 | Message grouping checks SenderId and Role | Handles role switches mid-conversation |
| 09-02 | Own messages use green gradient | Visual distinction from purple user messages |
| 09-02 | Avatar size 28px | Within CONTEXT.md's 24-32px recommendation |

## Session Continuity

**Last session:** 2026-01-21T03:10:47Z
**Stopped at:** Completed 09-02-PLAN.md (Phase 9 COMPLETE)
**Resume file:** None

## Next Steps

1. Plan Phase 10 - Backend Integration (BACK-01, BACK-02, BACK-03)
2. Execute Phase 10 plans
3. Complete Milestone v1.1 Users in Web UI

---
*State initialized: 2026-01-19*
*Last updated: 2026-01-21 — Completed 09-02-PLAN.md*
