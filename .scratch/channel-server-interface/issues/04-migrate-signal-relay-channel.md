# 04 — Migrate the signal-relay channel

**What to build:** The signal-relay channel runs through the shared registration with the broadcast policy.

This is the ticket that proves a transport-specific payload field survives consolidation. This channel is the only one that populates the per-message agent config patch, which previously widened its own emitter's parameter list. Now the caller builds the notification directly with named properties, so the field is an ordinary property on the shared payload and no interface widens to accommodate it.

Building the payload with named properties also removes a latent hazard: the old positional emitters carried adjacent optional string parameters that could be transposed at a call site with no compiler complaint.

This channel computes a liveness property today that nothing in production reads. That property disappears rather than being carried across.

**Blocked by:** 02

**Status:** done

- [x] The signal-relay channel is registered through the shared call with the broadcast policy.
- [x] The per-message config patch still rides an outbound message notification end to end.
- [x] The notification is built with named properties at the call site.
- [x] The channel's unread liveness property is gone, not reimplemented.
- [x] The signal-relay project no longer contains its own transport tool, error filter or emitter.
- [x] The channel's emitter test is narrowed to its own payload shape, covering the config patch field; its liveness assertions are removed.
- [x] Existing tests covering agent-initiated streaming and conversation creation still pass.
- [x] The existing channel conformance theory still passes.
