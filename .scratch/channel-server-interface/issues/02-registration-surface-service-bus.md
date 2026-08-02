# 02 — Registration surface, proved on the service-bus channel

**What to build:** The tracer bullet. One registration call wires a complete channel surface, and the service-bus channel becomes the first server to run entirely through it — its long-poll transport tool, its error filter and its notification emitter all come from the shared module instead of its own project.

The service-bus channel goes first because it is the one that genuinely acts on the answer: it abandons a broker message when nobody is listening, so at-least-once redelivery is preserved. That makes the emitter's return value load-bearing from the first migration rather than ignored, and proves the whole path end to end.

The registration call extends the model-context-protocol server builder, not the service collection — the transport tool and the error filter have to join the builder chain, and the inbox and emitter are registered through the builder's services from there. It takes the delivery policy as a required argument with no default, so a new transport cannot silently inherit a buffering behaviour nobody chose.

The shared error filter must preserve the existing rule exactly: a long-poll cancellation propagates as the abort it is, because mapping it to an error result would hand the agent's pump something to retry on. Every other exception is logged and returned as an error result.

After this ticket the service-bus project no longer contains its own copy of the transport tool, the error filter or the emitter.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] A single registration call takes a delivery policy and wires the inbox, the transport tool, the error filter and the emitter.
- [ ] The registration call extends the model-context-protocol server builder.
- [ ] The delivery policy argument is required; omitting it does not compile.
- [ ] A long-poll cancellation still propagates rather than becoming an error result.
- [ ] Any other tool exception is still logged and returned as an error result.
- [ ] The service-bus channel is registered through the shared call with the broadcast policy.
- [ ] The service-bus channel's own transport tool, error filter and emitter are deleted from its project.
- [ ] The service-bus processor still abandons a broker message when no live subscriber exists, and still completes it when one does — behaviour unchanged.
- [ ] The service-bus channel's existing tests pass, with its emitter test narrowed to its own payload shape and its liveness assertions removed.
- [ ] The existing channel conformance theory still passes for all six servers.
