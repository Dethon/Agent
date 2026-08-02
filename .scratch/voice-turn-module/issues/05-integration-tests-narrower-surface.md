# 05 — Integration tests onto the narrower surface

**What to build:** The host and wake-arbitration integration files still test the voice turn the way it looked before this spec. Three places settle a turn by calling a signal method directly — a route that no longer exists on the turn module's interface, and one that never matched what the agent actually does. After this ticket those places drive the real path: begin a segment, complete it, end the stream. What the test proves and what production does become the same thing.

The rest of the restructure follows the narrower surfaces: captures and gates reached through their modules rather than assembled in the test, and the turn reached through the property rather than through session methods that are now private.

This is a separate ticket because it is the only part of this spec that collides with other planned work. Tickets 03 and 04 each carry the minimal edit needed to keep these files green, so this one can be rebased or deferred without blocking anything else here.

**Blocked by:** 03 — VoiceTurn, the segment token, and the reply-tool collapse. 04 — CaptureSession and the coordinator rewrite.

**Status:** ready-for-agent

- [ ] The three places that settle a turn by signalling directly drive the real path instead.
- [ ] The integration files reach captures, gates and the turn through their modules, not through hand-assembled state.
- [ ] The room-noise coverage in the host file still passes, unchanged in intent from before ticket 01.
- [ ] Wake arbitration coverage is unaffected: a capture opened through the capture module still carries the chunk history that rule B needs.
- [ ] No test asserts on a field that this spec made private.

**Sequencing.** The host integration file is 2,215 lines and the wake-arbitration file is also rewritten by the channel-server plan's ticket 07, which touches both. Whichever lands second rebases onto the first. Do not run the two in parallel.
