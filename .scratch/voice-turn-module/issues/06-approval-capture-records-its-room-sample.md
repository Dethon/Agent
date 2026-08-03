# 06 — The approval capture records its room sample

**What to build:** The approval mic reads the room-noise memory but never writes to it. Ticket 02 gave it the capped floor; this ticket makes it pay in as well as take out.

Every other capture the hub runs teaches the memory something. A capture that heard no speech spent its whole window measuring the background, and one that ended on trailing silence measured it over the run that ended it — `SilenceGateFactory.RecordCaptureClose` already knows how to read either from the frozen gate statistics. The approval capture produces both shapes routinely: an unanswered prompt times out with a full window of background, and an answered one ends on its trailing run. None of it is recorded, because the approval capture closes with a bare `session.CloseCapture()` in the tool rather than through the capture module.

This costs the memory on exactly the satellites that use approvals most. `RoomLevelRetentionSeconds` expires samples so a reading stops capping a room it no longer describes; a conversation that runs wake turn → approval → approval contributes one sample where it should contribute three, and a satellite whose recent activity was mostly approvals can arrive at the next wake turn with an empty memory and an uncapped floor. That is the state ticket 02 exists to avoid, reached by attrition instead of by omission.

The fix is a write at the approval close, in the tool's `finally` so a cancelled approval still records what it measured. Whether it goes through `CaptureSession` or straight to the factory is an open question: the approval capture is a one-shot with no turn, no follow-up window and no indicator events, so the module may not fit it. Do not restructure the tool's static entry point or its service-locator shape to make it fit — that was ruled out in the spec and stays out.

**Why this is not ticket 02's job:** ticket 02 was scoped to the read side, and the spec named the floor inheritance as the only intended behaviour change in that work. This is the write side, it is a second behaviour change, and it was already true before the spec started — the approval site has never recorded a sample. Splitting it keeps that change reviewable on its own.

**Blocked by:** 01 — Per-satellite gate factory. 02 — Approval capture inherits the room-noise floor.

**Status:** ready-for-agent

- [ ] A failing test first: an approval capture that times out with no speech leaves a room sample behind, and the next gate built for that satellite is capped by it.
- [ ] The sample is recorded from the frozen gate statistics at the close, by the same rule every other capture uses — a capture that established nothing about silence still records nothing.
- [ ] Recorded in the tool's `finally`, so a cancelled or arbitration-abandoned approval records what it measured rather than discarding it.
- [ ] The approval tool keeps its static entry point and its service-locator shape.
- [ ] The comment explaining which end reasons carry a usable measurement stays in one place — do not restate the rule at the approval site.
- [ ] The approval tool's existing unit coverage passes unchanged, including the arbitration-abort and per-satellite-override tests.

**Open question for the implementer.** If the approval capture goes through `CaptureSession`, that module gains a caller with no turn to start and no indicator to write, and its `Open` would have to stop marking turn start. If it calls the factory directly, `RecordCaptureClose` has two call sites again — which is acceptable, since the rule they share lives in the factory and neither site restates it. Pick one, and say in the commit which and why.
