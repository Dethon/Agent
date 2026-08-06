# 04 — The stream-handle machinery deletes

**What to build:** nothing new — this ticket is entirely subtractive, and finishing it is what
makes the previous one a deepening rather than an added field.

With the turn key deciding which turn a reply answers, the machinery the hub used to infer it
has no remaining question. The per-conversation map of stream handles, the handle type, its
reference check and the epoch guard on stream ends all go. The segment token and its epoch stay:
playback callbacks genuinely outlive the turn that queued them, which is a different question
and still needs an answer.

The hand-written gate that suppresses turn-anchored latency metrics when no dispatch stamp was
consumed also goes. It exists because a scheduled delivery reaching the live reply path would
otherwise report the age of the last real turn as its own latency — and a reply that is not this
turn's no longer reaches the metrics at all.

**Blocked by:** 03 — Voice stamps the turn key and classifies replies.

**Status:** ready-for-agent

- [ ] The per-conversation stream-handle map, the handle type, its reference check and the
      stream-end epoch guard are removed.
- [ ] The segment token and its epoch are untouched.
- [ ] The hand-written metrics gate keyed on the consumed dispatch stamp is removed, and the
      turn-anchored latency metrics still publish for real turns only.
- [ ] Behaviour is unchanged: ticket 03's four tests pass without modification.
- [ ] No test names a deleted member.
