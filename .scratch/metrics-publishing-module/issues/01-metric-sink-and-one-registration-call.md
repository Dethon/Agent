# 01 — Metric sink, and one call to wire metrics publishing

**What to build:** A Redis blip stops costing the user a voice turn. Today the voice host registers the Redis transport directly as its metrics publisher, so a publish is a live network round trip: the satellite host publishes its speech-to-text latency inside the try block whose catch discards the transcript, and publishes its error event inside that catch, from where it escapes into the follow-up conversation loop and kills the conversation task while the connection to the satellite stays up. After this ticket, neither can happen.

The fix is structural, not a guard. Split the two roles the current contract conflates. A **metric sink** sends an event and is allowed to fail; the Redis transport becomes its one adapter. A **metrics publisher** is the fire-and-forget thing callers hold; the buffered publisher becomes its only registered implementation and drains into the sink on its background reader, logging whatever the sink refuses.

The sink contract belongs in the infrastructure layer, not the domain contracts folder — the domain layer never consumes a sink. Per ADR 0001, having one adapter is not a reason to make it a bare delegate.

Hosts stop assembling this by hand. One registration call takes the service name and wires the sink, the buffered publisher and the heartbeat service together. Both hosts adopt it. There is then no path by which a host resolves a bare sink as its caller-facing publisher, which is the whole of the reported defect.

This ticket is the expand half of an expand–contract sequence, so nothing else has to change yet. Add the void publish method to the publisher contract as a default implementation that delegates to the existing awaitable one. Every current call site, adapter and test fake keeps compiling and keeps behaving as it does today, and later tickets migrate them in batches. Add the no-op publisher that those tickets will coalesce to.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A host registration theory boots each host's real registration module and asserts the resolved metrics publisher is the buffered one. Written first, and seen to fail against today's voice wiring.
- [ ] A metric sink contract exists in the infrastructure layer, sending an event asynchronously and permitted to throw.
- [ ] The Redis transport implements the sink contract and no longer implements the publisher contract.
- [ ] The buffered publisher takes a sink and keeps its bounded channel, its drop-on-full behaviour, its warning log for a dropped event, and its bounded drain on disposal.
- [ ] A throwing sink does not escape a publish.
- [ ] A throwing sink does not stop the drain loop from handling later events.
- [ ] A full buffer drops and logs rather than throwing or blocking.
- [ ] One registration call takes a service name and registers the sink, the buffered publisher and the heartbeat service.
- [ ] Both the agent host and the voice host use that call and nothing else to wire metrics.
- [ ] The publisher contract exposes a void publish method, defaulted to delegate to the awaitable one, so existing callers and test fakes are untouched.
- [ ] A shared no-op publisher instance exists.
- [ ] The whole solution builds and every existing test passes with no edits to call sites outside the metrics module and the two host registrations.
