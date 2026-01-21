# Roadmap: Agent WebChat v1.1

**Milestone:** Users in Web UI
**Phases:** 8-10 (continues from v1.0's phase 7)
**Requirements:** 9 total
**Depth:** Comprehensive

## Overview

This milestone adds user identity to WebChat. Users set a username (persisted locally), messages show who sent them, and agents know who they're talking to. Three phases: identity foundation, message UI, backend integration.

---

## Phase 8: User Identity

**Goal:** Users can establish their identity in the app.

**Dependencies:** None (foundation phase)

**Plans:** 2 plans

Plans:
- [x] 08-01-PLAN.md — State management infrastructure (UserIdentityStore, UserConfig model, users.json config)
- [x] 08-02-PLAN.md — UI component and integration (UserIdentityPicker, Effect, header integration)

**Requirements:**
- USER-01: User can set username via compact picker UI
- USER-02: Username persists in localStorage across sessions
- USER-03: Avatar determined by hardcoded username->avatar lookup

**Success Criteria:**
1. User sees username picker when no username is set
2. User can type a username and confirm it
3. After setting username, picker closes and username is stored
4. Refreshing the page preserves the username (no picker shown)
5. Avatar is automatically determined based on username (no separate selection)

---

## Phase 9: Message Attribution

**Goal:** Users can see who sent each message in the chat.

**Dependencies:** Phase 8 (needs username and avatar from identity)

**Plans:** 3 plans

Plans:
- [x] 09-01-PLAN.md — Data model and avatar component (ChatMessageModel sender fields, AvatarHelper, AvatarImage component)
- [x] 09-02-PLAN.md — Message UI integration (ChatMessage avatar layout, MessageList grouping, own message styling)
- [x] 09-03-PLAN.md — Gap closure: Wire sender identity into message creation, right-align user messages

**Requirements:**
- MSG-01: Messages display sender's username
- MSG-02: Messages display sender's avatar (from lookup)
- MSG-03: User's own messages visually distinguished from others

**Success Criteria:**
1. Each message shows the sender's username (on hover)
2. Each message shows the sender's avatar image (left of bubble)
3. User's own messages appear visually different (green gradient background)
4. Agent messages remain visually distinct from user messages (full-width, no avatar)

---

## Phase 10: Backend Integration

**Goal:** Backend knows who is sending messages for personalized responses.

**Dependencies:** Phase 8 (needs username to send)

**Plans:** 2 plans

Plans:
- [x] 10-01-PLAN.md — Server-side user registration (RegisterUser hub method, Context.Items storage, registration guard)
- [x] 10-02-PLAN.md — Message flow and agent personalization (senderId in messages, reconnection handling, username in prompts)

**Requirements:**
- BACK-01: Username sent to backend on SignalR connection
- BACK-02: Username included in message payloads to server
- BACK-03: Agent prompts include username for personalization

**Success Criteria:**
1. SignalR connection includes username in handshake/registration
2. Messages sent to server include sender's username
3. Agent responses address user by name when contextually appropriate
4. Agent maintains awareness of who it's talking to across conversation

---

## Progress

| Phase | Name | Requirements | Status |
|-------|------|--------------|--------|
| 8 | User Identity | USER-01, USER-02, USER-03 | Complete |
| 9 | Message Attribution | MSG-01, MSG-02, MSG-03 | Complete |
| 10 | Backend Integration | BACK-01, BACK-02, BACK-03 | Complete |

**Parallelization Note:** Phases 9 and 10 both depend on Phase 8 but are independent of each other. They could potentially be executed in parallel after Phase 8 completes.

---
*Roadmap created: 2026-01-21*
*Phases continue from v1.0 (ended at phase 7)*
