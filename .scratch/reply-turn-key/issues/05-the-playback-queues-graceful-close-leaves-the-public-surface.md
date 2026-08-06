# 05 — The playback queue's graceful close leaves the public surface

**What to build:** reading the playback queue's public surface tells you how a satellite
connection actually ends.

Production ends one three ways in one order from one place: the link-drop close, then the sweep
of everything the loop never played, then disposal. The queue's fourth verb — "stop accepting
work, play what is already queued, then stop" — has no production caller at all. Shutdown does
not use it either: cancelling the run token stops the loop and the sweep settles the rest. It
is on the public surface for tests alone, and it is a behaviour, not just an accessibility
difference, so the tests that use it have to close some other way.

Independent of the turn key work; can run alongside ticket 01.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The graceful close is no longer part of the queue's public surface.
- [ ] Tests that used it to let the loop finish close by cancelling the token they already pass
      to the run loop, and await the loop as they do today.
- [ ] Tests that genuinely want link-drop semantics call the verb production calls, and their
      assertions are updated to the outcomes that produces.
- [ ] Every playback outcome the queue promises is still asserted somewhere: heard to the end,
      cut short, broken, refused, and discarded because the connection died.
- [ ] No production call site changes.
