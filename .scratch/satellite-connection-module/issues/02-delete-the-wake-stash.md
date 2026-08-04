# 02 — Delete the wake stash

**What to build:** when a satellite's own wake detection fires, what it reports about that
wake — how loud, how confident, what triggered it, how loud the room was just before —
reaches the **wake turn** it opens by being passed as an argument, not by being left on
the **satellite session** for a callback in another file to pick up.

For a household member nothing changes: a wake still opens a turn reported with that
wake's loudness, the room level the satellite measured still caps the endpointing floor,
a **follow-up turn** is still never reported as if it had its own wake, and an older
satellite that announces with an audio-start frame and sends no wake metadata still
works exactly as before.

What goes away is the stash and the rules that existed only to protect it. Today the read
loop writes the announcement onto the session, a callback two files away consumes it
single-use when the microphone opens, and — because nothing connects those two ends —
the read loop then has to drop whatever was left over, guarded by a comment explaining
that a stale value would report one turn's loudness against the next and skew the gate
calibration that reads it. Passing the announcement removes all three.

The voice host stays a single file in this ticket. Its integration suite stays green
throughout and is the evidence that the behaviour held.

**Blocked by:** None — can start immediately. Runs in parallel with ticket 01.

**Status:** resolved

- [x] The **wake announcement** type and its defensive parser move to the Wyoming protocol
      area, alongside the other wire types, with the parser exposed as a static read
      operation on the type.
- [x] The parser's tolerance is unchanged and still covers absent, null and wrong-typed
      values for every field, including a missing data object entirely. Its existing unit
      tests move with it under a name matching its new owner, with no assertion changed.
- [x] The capture session's single open operation is replaced by two named ones: opening a
      wake turn, which takes the announcement, and opening a follow-up turn, which takes
      nothing. The follow-up opening never publishes a wake-triggered metric.
- [x] Recording the satellite's reported room level moves into the wake-turn opening,
      immediately before the gate is built, beside the capture-close recording already
      there.
- [x] The conversation coordinator's wake announcement takes the announcement and passes
      it to the wake-turn opening. Its early return when a turn is already open discards
      the argument, which is what replaces the explicit drop step.
- [x] The satellite session's wake stash is deleted — both the note operation and the
      single-use consume operation. Nothing in the codebase references them afterwards.
- [x] The legacy audio-start path announces the wake with no announcement, records a zero
      room level, and behaves identically — the room-noise memory already discards a
      non-positive sample as an absent measurement rather than a silent room. Cover this
      with a test.
- [x] The conversation coordinator's unit tests are updated for the two named openings and
      otherwise unchanged. *Four assertions changed shape:* they observed a follow-up opening
      through the old single `onOpened` callback, which the split removes. Each now observes
      the same behaviour through the coordinator's own surface (the live capture, the
      dispatch count, the wake-hook count) rather than a marker the production code no longer
      emits.
- [x] The whole voice integration suite passes without modification, including the
      multi-satellite arbitration tests. *One line had to give:* the frame-order test read the
      stash back through `TryConsumeWakeSignal`, which this ticket deletes, so the two boxes
      contradict each other. The claim it made — nothing is left over for the next wake — is
      now structurally impossible and covered directly by the coordinator's
      `OnWake_TurnAlreadyOpen_DiscardsTheSecondAnnouncement`.
