# Phase 4: SignalR Integration - Context

**Gathered:** 2026-01-20
**Status:** Ready for planning

<domain>
## Phase Boundary

Route SignalR hub events through the unidirectional state pattern via HubEventDispatcher. Handle reconnection with stream resumption. Manage event subscription lifecycle to prevent leaks.

</domain>

<decisions>
## Implementation Decisions

### Reconnection behavior
- Automatic reconnection with exponential backoff (1s, 2s, 4s... up to cap)
- Unlimited retry attempts — never give up
- Server buffers messages during disconnect, client catches up on reconnect
- If disconnected too long and buffer expired, refresh full state from server

### Stream resumption
- Client tracks sequence numbers, requests only missing content on reconnect
- Auto-scroll to new content only if user was already at bottom before disconnect
- 5-day buffer on server (few clients, memory not a concern)

### Event routing
- Failed events: silent drop with logging, no retry or user notification
- Strict event ordering — queue and process sequentially
- Per-topic dispatchers for the active topic
- Lightweight listener across all topics for unread badge updates (non-active topics still need message count events)

### Connection feedback
- Status indicator (small icon/dot) showing connection state
- Show all states: green (connected), yellow (reconnecting), red (disconnected)
- No manual reconnect button — unlimited auto-retry means keep trying forever
- Disable input field during disconnection (grey out, prevent typing)

### Claude's Discretion
- UI transition when resuming stream (seamless append vs visual indicator)
- Exact dispatcher disposal timing for inactive topics
- Exponential backoff cap timing
- Status indicator placement (header vs sidebar)

</decisions>

<specifics>
## Specific Ideas

- Unread message badges must update even for non-selected topics — dispatcher architecture must support this
- Sequence tracking enables efficient resumption without full replay

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 04-signalr-integration*
*Context gathered: 2026-01-20*
