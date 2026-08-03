# 02 — Approval capture inherits the room-noise floor

**What to build:** When the agent asks a voice user to confirm something, the microphone that listens for the answer behaves like the microphone that heard the question. Today it does not: the wake and follow-up capture caps its noise floor with the quietest recent reading from the room, and the approval capture does not. So a user answering a confirmation is endpointed against a different, higher floor than the wake turn they were answering seconds earlier, in the same room, with the same background.

An inflated floor is not cosmetic. It arms the adaptive regime, whose peak-drop backstop then reads normal syllable dynamics as background and cuts the user off mid-sentence. The approval prompt is where being cut off costs most — a truncated "sí, la de las tres" is a wrong answer to a question the agent asked.

After this ticket the approval capture obtains its gate from the same factory as every other capture, so the two cannot drift apart: there is nothing left at either call site to drift.

This is the only intended behaviour change in this spec.

**Blocked by:** 01 — Per-satellite gate factory.

**Status:** done

- [x] A failing test first: an approval capture on a satellite with a recorded room sample uses the capped floor, not the raw resolved threshold.
- [x] The approval capture obtains its gate from the factory instead of assembling a tracker inline.
- [x] The factory unit file asserts that a gate built for a capture and a gate built for an approval are identical, including the room cap.
- [x] The approval tool's unit file asserts the capture is built through the factory. It already builds a real service collection and calls the tool's entry point, so the factory is reachable by registration.
- [x] The approval capture keeps its chunk history, so wake arbitration can still ask it retrospectively what it heard during another satellite's wake-word span.
- [x] The comment recording why the approval mic needs that history survives.

**Accepted trade:** neither test drives real audio through an approval capture and observes the endpointing decision change. That would mean the two-thousand-line host integration file, which the channel-server plan is also rewriting. This pair pins the wiring and the resolution, not the acoustic outcome. Knowingly accepted; there is no follow-up ticket for it.
