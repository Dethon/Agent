# 01 — Per-satellite gate factory

**What to build:** Every live capture on a satellite gets its endpointing gate from one place. Today the gate is assembled by hand at the call site, which is how two call sites came to resolve it differently. After this ticket the wake and follow-up capture asks a factory for a gate, and the factory is the only thing that knows how a gate is put together for a given satellite.

The factory is registered once for the process and owns the per-satellite room-noise memory that currently lives on the connection host. That memory is keyed by satellite precisely so it survives a reconnect — a room does not change because the TCP link blipped, and a reconnect is exactly when a satellite is least able to measure itself. A per-connection object is the wrong lifetime for it, so it belongs here rather than on the session or on the capture module that arrives in ticket 04.

Both halves of the memory move together: the reads that cap the noise floor, and the write that records a room sample when a capture closes. The connection host is left holding no room-noise state at all.

The factory exposes a single build method taking the satellite. There is deliberately no gate-purpose parameter: after ticket 02 the two remaining capture sites build an identical gate, and a parameter that only ever takes one value is a place for them to diverge again. If a real difference appears later it comes back with a test that names the difference.

Nothing observable changes in this ticket. The approval site still builds its own gate; ticket 02 moves it.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A gate factory is registered for the process and holds the per-satellite room-noise memory, keyed so it outlives any single connection.
- [ ] The factory resolves per-satellite endpointing overrides against the global settings, and applies the room-noise cap so it can only ever lower the floor.
- [ ] The wake and follow-up capture obtains its gate from the factory rather than assembling a tracker inline.
- [ ] The room sample taken when a capture closes is recorded through the factory; the connection host no longer holds room-noise state.
- [ ] A new unit file pins the resolution: per-satellite overrides beat the globals, and a recorded room sample lowers a later gate's floor.
- [ ] The existing room-noise coverage in the host integration file passes unchanged — that is what says the move changed nothing.
- [ ] The comment explaining why a capture cannot measure the background it opens on top of moves with the code it describes.
