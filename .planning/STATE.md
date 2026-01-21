# Project State: Agent WebChat

## Project Reference

See: `.planning/PROJECT.md` (updated 2026-01-21)

**Core Value:** People can have personalized conversations with agents in shared topics
**Current Focus:** Milestone v1.1 Users in Web UI — Phase 10 (Backend Integration) IN PROGRESS

**Key Files:**
- `.planning/PROJECT.md` - Project definition
- `.planning/ROADMAP.md` - v1.1 roadmap (phases 8-10)
- `.planning/REQUIREMENTS.md` - v1.1 requirements with traceability
- `.planning/MILESTONES.md` - Milestone history
- `.planning/milestones/v1.0-ROADMAP.md` - Archived v1.0 roadmap
- `.planning/milestones/v1.0-REQUIREMENTS.md` - Archived v1.0 requirements

## Current Position

**Milestone:** v1.1 Users in Web UI
**Phase:** 10 of 3 (Backend Integration) - IN PROGRESS
**Plan:** 1 of 2 complete
**Status:** In progress
**Last activity:** 2026-01-21 — Completed 10-01-PLAN.md (server registration)

**Progress:**
```
v1.1 Users in Web UI: [██████████████████████░░] 86% (6/7 plans)

Phase 8: User Identity       [████████] 2/2 plans complete
Phase 9: Message Attribution [████████] 3/3 plans complete
Phase 10: Backend Integration [████░░░░] 1/2 plans complete
```

## Phase Summary

| Phase | Goal | Requirements | Status |
|-------|------|--------------|--------|
| 8 | Users can establish their identity | USER-01, USER-02, USER-03 | COMPLETE |
| 9 | Users can see who sent each message | MSG-01, MSG-02, MSG-03 | COMPLETE |
| 10 | Backend knows who is sending messages | BACK-01, BACK-02, BACK-03 | In Progress |

## Phase 8 Success Criteria

1. [x] User sees avatar picker when no username is set (shows "?" placeholder)
2. [x] User can select a username from predefined list (dropdown menu)
3. [x] After selecting user, picker shows their avatar
4. [x] Refreshing the page preserves the username (localStorage persistence)
5. [x] Avatar is automatically determined based on username (users.json mapping)

## Phase 9 Success Criteria

1. [x] Messages display sender's username (hover tooltip on message bubble)
2. [x] Messages display sender's avatar (28px circular, right of bubble for users)
3. [x] User's own messages visually distinguished (green gradient vs purple)
4. [x] Avatar grouping: first message in consecutive group shows avatar, others show placeholder
5. [x] Agent messages: full-width, no avatar column, left-aligned
6. [x] User messages right-aligned with avatar on right

## Phase 10 Success Criteria (Plan 01)

1. [x] RegisterUser validates userId against server-side users.json
2. [x] SendMessage rejects calls from unregistered connections
3. [x] UserConfigService provides synchronous user lookup (lazy-loaded, cached)
4. [x] Per-connection identity stored in Context.Items

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

**From 09-03 (gap closure):**
- SendMessageEffect injects UserIdentityStore and populates sender fields
- User messages right-aligned with avatar on right (flex-direction: row-reverse)
- Sender identity local-only; cross-client deferred to Phase 10

**From 10-01:**
- UserConfigService loads users.json server-side with lazy initialization
- RegisterUser hub method validates userId and stores in Context.Items
- SendMessage guards against unregistered connections
- Per-connection identity via Context.Items["UserId"] and Context.Items["Username"]

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
| 09-03 | User messages right-aligned | Chat app convention (own on right) |
| 09-03 | Local-only sender identity for Phase 9 | Backend changes deferred to Phase 10 |
| 09-03 | Sender fields null when no user selected | Graceful fallback to initials avatar |
| 10-01 | Lazy loading for users.json | Defer file read until first access |
| 10-01 | Context.Items for identity storage | Per-connection state (not ambient/static) |
| 10-01 | UserConfigService in AddWebClient only | Only needed for web chat interface |

## Session Continuity

**Last session:** 2026-01-21
**Stopped at:** Completed 10-01-PLAN.md (server registration)
**Resume file:** None

## Next Steps

1. Execute 10-02-PLAN.md (client integration and agent personalization)
2. Complete Milestone v1.1 Users in Web UI

---
*State initialized: 2026-01-19*
*Last updated: 2026-01-21 — Completed 10-01-PLAN.md (Phase 10 Plan 01 COMPLETE)*
