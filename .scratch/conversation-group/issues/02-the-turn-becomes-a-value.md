# 02 — The turn becomes a value

**What to build:** A reply update can no longer travel with delivery targets belonging to
a different message, and the first-reply tracker can no longer be absent.

Introduce a turn record carrying the originating channel, the channel message, the
resolved delivery targets and the first-reply tracker. The chat monitor mints one per
turn, at the point the turn is dequeued and starts, so the tracker's window is unchanged.
The record that carries a reply update to delivery carries the turn instead of three
parallel fields, which makes the tracker non-nullable and the update-to-targets pairing
unrepresentable.

The message index survives this ticket untouched. The turn does not carry it, and nothing
about how the group is anchored changes yet.

Behaviour is identical. This is the second of three preparatory steps.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] A turn record exists carrying the origin channel, the message, the resolved targets and the tracker.
- [ ] The reply-update record carries the turn rather than separate targets and tracker fields.
- [ ] The tracker is non-nullable, and the delivery loop reads it from the turn.
- [ ] The tracker is still created when the turn starts, so first-reply latency measures the same window as before.
- [ ] The private methods that took targets and the tracker as separate parameters take the turn instead.
- [ ] The existing monitor test suite passes unchanged.
