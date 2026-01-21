# Phase 9: Message Attribution - Context

**Gathered:** 2026-01-21
**Status:** Ready for planning

<domain>
## Phase Boundary

Display who sent each message in the chat interface. Messages show sender's username and avatar. User's own messages are visually distinguished from others. Agent messages remain distinct from user messages.

</domain>

<decisions>
## Implementation Decisions

### Message Layout
- Avatar positioned to the left of the message bubble (classic chat style)
- Username shown on hover only (not displayed by default)
- Agent messages use distinct layout: full-width, no avatar column

### Visual Distinction
- User's own messages distinguished by different background color (not alignment)
- Own messages still show avatar for consistency with other users
- Agent messages have different background color from user messages
- No explicit bot icon or label — styling alone distinguishes agents

### Avatar Display
- Small size (24-32px) for compact message density
- Circular shape to match UserIdentityPicker in header (Phase 8 consistency)
- First message only in consecutive groups — placeholder space on subsequent messages
- Fallback: first letter of username in colored circle when image fails to load

### Claude's Discretion
- Exact background colors for own messages vs others vs agents
- Spacing between avatar and message bubble
- Hover behavior implementation for username display
- Placeholder space styling for grouped messages

</decisions>

<specifics>
## Specific Ideas

- Consistency with Phase 8: avatar shape matches the UserIdentityPicker circular style
- Username on hover keeps the UI clean while still providing attribution when needed

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 09-message-attribution*
*Context gathered: 2026-01-21*
