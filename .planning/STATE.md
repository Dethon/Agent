# Project State: Agent WebChat

## Project Reference

See: `.planning/PROJECT.md` (updated 2026-01-21)

**Core Value:** People can have personalized conversations with agents in shared topics
**Current Focus:** Milestone v1.1 Users in Web UI — defining requirements

**Key Files:**
- `.planning/PROJECT.md` - Project definition
- `.planning/MILESTONES.md` - Milestone history
- `.planning/milestones/v1.0-ROADMAP.md` - Archived v1.0 roadmap
- `.planning/milestones/v1.0-REQUIREMENTS.md` - Archived v1.0 requirements

## Current Position

**Milestone:** v1.1 Users in Web UI
**Phase:** Not started (defining requirements)
**Status:** Defining requirements
**Last activity:** 2026-01-21 — Milestone v1.1 started

**Progress:**
```
v1.1 Users in Web UI: [░░░░░░░░░░░░░░░░░░░░░░░░] 0% (defining requirements)
```

## v1.1 Target Features

- Lightweight user identity (username + avatar, localStorage)
- Shared conversations (global topics, real-time sync)
- Agent personalization (username in prompts)

## Success Criteria

1. Users identified in chat (username + avatar on messages)
2. Personalized agent responses (agent knows and uses your name)
3. Working shared topics (two browsers, same topic, real-time sync)

## Accumulated Context

**From v1.0:**
- State management uses Store<T> with BehaviorSubject
- Components extend StoreSubscriberComponent for subscriptions
- HubEventDispatcher routes SignalR events to store actions
- 50ms render throttling prevents UI freezes

---
*State initialized: 2026-01-19*
*Last updated: 2026-01-21 — v1.1 milestone started*
