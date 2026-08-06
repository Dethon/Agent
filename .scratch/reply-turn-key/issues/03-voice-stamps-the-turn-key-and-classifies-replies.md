# 03 — Voice stamps the turn key and classifies replies

**What to build:** the three symptoms in the spec stop.

A satellite that redialled after its link dropped answers the next thing the user says, instead
of going quiet for two minutes. An answer the hub gave up on stays unsaid instead of being
spoken glued to the front of the next one. A timer or scheduled message landing mid-conversation
is heard and nothing else — the user keeps the follow-up window they were owed, and the real
answer still arrives.

Voice mints the turn key as it dispatches a transcript, because it is the side that has to know
the value in advance. The turn is stamped with it in the same act that stamps the dispatch
timestamp, and whatever reply text the previous turn left buffered for that conversation is
dropped at the same moment — flushing at dispatch rather than on a mismatched chunk means the
buffer is cleared even when the abandoned run never sends anything else.

The reply speaker then applies four cases on the live path, before anything is buffered or
queued:

- key matches the turn's — the answer the user is waiting for; unchanged behaviour
- key differs, turn was agent-initiated — spoken, no stream opened, no segment registered, the
  turn is not settled
- key differs, turn was a user turn — discarded and logged; nothing appended
- no key at all — only reachable if the echo is broken, so treat it as the current turn's and
  publish an error event, so the breakage shows up as itself rather than as satellites that
  stopped answering

The existing stream-handle path stays in place for now; with the classification gating ahead of
it, it only ever sees replies whose key matches, where its reference check is a no-op. Ticket 04
removes it.

**Blocked by:** 02 — A turn's replies carry its turn key.

**Status:** ready-for-agent

- [x] The transcript dispatch mints a turn key, stamps it on the turn, and drops the previous
      turn's buffered reply text for that conversation in the same act.
- [x] A reply whose key matches the stamped turn behaves exactly as today.
- [x] A reply whose key differs and whose turn was agent-initiated is spoken and leaves the live
      turn outstanding.
- [x] A reply whose key differs and whose turn was a user turn is discarded, logged, and appends
      nothing to the buffer.
- [x] A reply carrying no key is treated as the current turn's and publishes an error event.
- [x] A satellite that redials mid-answer settles its next turn normally rather than waiting out
      the reply timeout.
- [x] Four tests at the existing reply speaker seam, one per case, asserting the turn's settled
      result and what reached the synthesizer — not the internal maps or counters.
