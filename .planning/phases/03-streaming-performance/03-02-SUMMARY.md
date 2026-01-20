---
phase: 03-streaming-performance
plan: 02
subsystem: ui
tags: [css-animations, streaming, visual-feedback, blazor]

# Dependency graph
requires:
  - phase: 02-state-slices/02-02
    provides: StreamingState with IsStreaming flag
provides:
  - CSS animations for streaming cursor (blinking | at end of text)
  - Typing indicator with animated dots (waiting for first token)
  - Error recovery CSS classes (styling only, wiring in Phase 4)
  - Full error banner styling
affects: [04-signalr-integration, 05-component-architecture]

# Tech tracking
tech-stack:
  added: []
  patterns: [css-only-animations, pseudo-element-cursor, staggered-dot-animation]

key-files:
  created: []
  modified:
    - WebChat.Client/wwwroot/css/app.css
    - WebChat.Client/Components/ChatMessage.razor

key-decisions:
  - "Blinking cursor uses CSS ::after pseudo-element on .message-content"
  - "Typing indicator shows only when IsStreaming AND content is empty"
  - "Streaming cursor shows only when IsStreaming AND content is not empty"
  - "Error recovery CSS classes defined now, component wiring deferred to Phase 4"

patterns-established:
  - "CSS-only visual feedback: hardware-accelerated animations without JavaScript re-renders"
  - "Conditional CSS class application based on streaming state"

# Metrics
duration: 2min
completed: 2026-01-20
---

# Phase 3 Plan 02: Streaming Visual Feedback Summary

**CSS-only visual feedback for streaming: blinking cursor, typing indicator, error recovery styling**

## Performance

- **Duration:** 2 min
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Blinking cursor animation appears at end of streaming message content
- Typing indicator with animated dots shows while waiting for first token
- Error recovery CSS classes defined for future error state UI (wired in Phase 4)
- All visual feedback is CSS-based (hardware-accelerated, no render overhead)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add CSS animations for streaming visual feedback** - `f172649` (feat)
   - Added streaming cursor keyframes (cursor-blink)
   - Added typing indicator with staggered dot animation (typing-pulse)
   - Added error recovery styling (.streaming-error, .streaming-reconnecting)
   - Added error banner styling (.error-banner)

2. **Task 2: Update ChatMessage component with streaming cursor** - `770b37a` (feat)
   - Modified GetMessageClass() to add streaming-cursor when streaming with content
   - Updated streaming indicator to show typing-indicator only when content is empty

## Files Modified

**CSS (1 file):**
- `WebChat.Client/wwwroot/css/app.css` - Added 152 lines of CSS for streaming visual feedback
  - `.streaming-cursor .message-content::after` - Blinking | cursor
  - `.typing-indicator` - Animated dots while waiting for first token
  - `.streaming-error` - Inline error with retry button styling
  - `.streaming-reconnecting` - Reconnecting state with small spinner
  - `.error-banner` - Full-width error banner after all retries fail

**Component (1 file):**
- `WebChat.Client/Components/ChatMessage.razor` - Updated to apply correct CSS classes
  - GetMessageClass() adds "streaming-cursor" when IsStreaming && has content
  - Typing indicator shows only when IsStreaming && content is empty

## Visual Feedback States

| State | Visual Indicator | CSS Class |
|-------|------------------|-----------|
| Waiting for first token | Animated bouncing dots + "Thinking..." | `.typing-indicator` |
| Streaming content | Blinking cursor at end of text | `.streaming-cursor` |
| Streaming complete | No indicator | (class removed) |
| Error (recoverable) | Error text + retry button | `.streaming-error` |
| Reconnecting | Small spinner + "Reconnecting..." | `.streaming-reconnecting` |
| Error (final) | Full-width banner | `.error-banner` |

## Decisions Made

- **CSS-only animations:** All visual feedback uses CSS animations (no JavaScript re-renders needed)
- **Pseudo-element cursor:** Blinking cursor uses `::after` on `.message-content` for clean DOM
- **Staggered dot animation:** Typing dots use negative animation-delay for wave effect
- **Error styling now, wiring later:** Error recovery CSS classes are defined but component wiring is deferred to Phase 4 (SignalR Integration) where error handling logic lives

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - CSS changes only.

## Next Phase Readiness
- Visual feedback infrastructure ready for Phase 3 Plan 03 (throttled rendering)
- Error recovery styling ready for Phase 4 SignalR error handling wiring
- ChatMessage component ready to be connected to StreamingStore in Phase 5

---
*Phase: 03-streaming-performance*
*Plan: 02*
*Completed: 2026-01-20*
