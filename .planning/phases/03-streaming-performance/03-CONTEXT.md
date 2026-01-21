# Phase 3: Streaming Performance - Context

**Gathered:** 2026-01-20
**Status:** Ready for planning

<domain>
## Phase Boundary

Ensure streaming updates (tokens arriving at 50+ per second) render efficiently without freezing the UI or causing unnecessary re-renders in unrelated components. Covers selective re-rendering, throttled updates, and thread-safe state mutations.

</domain>

<decisions>
## Implementation Decisions

### Throttle behavior
- Character animation: characters appear one by one within each 50ms render window
- Adaptive animation speed: faster when more characters buffered, slower when few
- Stream end handling: use same 50ms window (no special flush behavior)
- Fixed 50ms throttle interval (hardcoded, not configurable)
- Shared render tick: single 50ms timer batches all concurrent streams together
- Apply same character animation even within code blocks (consistency over precision)

### Scroll behavior
- Smart auto-scroll: only auto-scroll if user is already at bottom; stop if they scroll up
- No "jump to bottom" indicator when user scrolls away during streaming
- Smooth (animated) scroll for auto-scroll updates

### Progress indicators
- Typing indicator: animated dots (three bouncing/pulsing dots) while waiting for first token
- Blinking cursor at end of text during active streaming
- Cursor disappears when streaming completes

### Error recovery
- On streaming failure: keep partial text, show inline "Continue" or "Retry" button
- Auto-retry up to 3 times before showing final error state
- During retry attempts: subtle "Reconnecting..." text near the partial message
- After all retries fail: full-width error banner above message area with retry action

### Claude's Discretion
- Transition animation from typing indicator to message bubble
- Exact timing for adaptive animation speed calculation
- Retry backoff strategy (immediate vs exponential)
- Error banner styling and dismiss behavior

</decisions>

<specifics>
## Specific Ideas

- Character-by-character animation should feel like watching someone type, not jarring chunks
- Smooth scroll should be gentle, not distracting during streaming
- Error states should be recoverable without losing context of what was already received

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 03-streaming-performance*
*Context gathered: 2026-01-20*
