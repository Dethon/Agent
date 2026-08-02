# 07 — Migrate the voice channel

**What to build:** The voice channel runs through the shared registration with the broadcast policy, and its tests stop depending on a test seam carried in production code.

This is the largest migration. The voice emitter is currently left unsealed with a virtual method for one reason only, stated in its own comment: an integration-test subclass overrides it. That subclass appears at roughly fourteen sites across two integration files, one of them 2,215 lines. Those tests move to constructing a real inbox and draining it, which exercises the actual delivery path instead of an override of it.

The voice channel carries three transport-specific fields — a room location, a satellite id and a dismissed-alert marker — which previously widened its emitter's parameter list. As with the signal-relay channel, they become ordinary named properties on the shared payload. Two of them are adjacent optional strings, so the named-property form removes a real transposition hazard.

Watch the wake-arbitration tests specifically. They use a lock-protected variant of the emitter subclass because two satellite connections reach it concurrently. The inbox is already thread-safe, but whatever helper replaces that variant must be safe to call from both connections.

This channel computes a liveness property that nothing in production reads. It disappears rather than being carried across.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] The voice channel is registered through the shared call with the broadcast policy.
- [ ] The room location, satellite id and dismissed-alert fields still ride an outbound notification, set as named properties.
- [ ] The emitter subclass is gone from the test tree; those tests construct a real inbox and drain it.
- [ ] The wake-arbitration tests remain correct with two connections emitting concurrently.
- [ ] The channel's unread liveness property is gone, not reimplemented.
- [ ] The voice project no longer contains its own transport tool, error filter or emitter.
- [ ] The voice emitter test is narrowed to its own payload shape; its liveness assertions are removed.
- [ ] The full voice integration suite passes, including the satellite host and wake-arbitration files.
- [ ] The existing channel conformance theory still passes.

## Comments

Conflicts with the voice turn-lifecycle work planned separately, which rewrites the same two integration files. These cannot be worked in parallel; whichever lands second rebases onto the first.
