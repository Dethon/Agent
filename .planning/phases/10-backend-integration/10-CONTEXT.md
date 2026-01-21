# Phase 10: Backend Integration - Context

**Gathered:** 2026-01-21
**Status:** Ready for planning

<domain>
## Phase Boundary

Backend knows who is sending messages for personalized agent responses. Users register their identity after connecting, messages include sender info, and the agent receives username context. This phase wires up the server side — UI changes were completed in Phases 8-9.

</domain>

<decisions>
## Implementation Decisions

### Connection Identity
- Client sends identity via separate RegisterUser call after connection (not in handshake)
- Server tracks identity per-connection only (no per-topic tracking)
- Server blocks messages until RegisterUser is called — no anonymous messaging
- RegisterUser sends just user ID; server looks up username/avatar from its own users.json

### Message Payload
- Messages sent to server include explicit senderId field
- Server trusts client-provided senderId (no validation against registered connection)
- Broadcasts to other clients include just senderId — clients look up the rest locally
- Server persists senderId with message history in Redis

### Agent Personalization
- Just pass username to agent — no behavior changes required
- Agent receives the information but uses it naturally (no forced greeting patterns)

### Identity Changes
- User can call RegisterUser again to switch identity mid-session
- Historical messages keep original sender attribution (no updates on identity change)
- Agent conversation context preserved on identity change (just knows new name)

### Claude's Discretion
- Exact SignalR hub method signatures
- Error message wording for unregistered connections
- How users.json is loaded/cached on server

</decisions>

<specifics>
## Specific Ideas

No specific requirements — open to standard SignalR patterns for the implementation.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 10-backend-integration*
*Context gathered: 2026-01-21*
