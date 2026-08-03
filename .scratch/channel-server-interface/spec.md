# Spec — Channel Server Interface

## Problem Statement

Adding a new transport to Ziggurat is advertised as cheap: the agent side needs no changes, because the channel connection feature-detects every optional tool. The server side is where the cost actually sits, and it is invisible.

"Being a channel server" is an unwritten checklist. A developer adding a transport must know to register a channel inbox, copy a channel-receive tool into their own project, copy a call-tool filter that maps errors but deliberately rethrows cancellation, wire a notification emitter, choose a buffering policy, decide whether to expose agent registration, and decide whether to support conversation creation. None of this is written down as a type. It is learned by reading another channel server and copying it.

The cost is measurable. The same stale-subscriber defect was fixed in three separate rounds across six servers. The first round fixed two of the six. The second round's own commit message records that two of the four remaining servers were gating production behaviour on the wrong liveness signal: one completed a broker message instead of abandoning it, defeating at-least-once redelivery, and another silently buffered a message it should have dropped. The third round retuned the shared freshness constant. Every one of the six migrations had landed the same bug independently.

All six now compute liveness identically, but only by convention. An eighteen-line doc comment warns future readers which liveness question is the right one to ask. That comment is a warning label standing in for a module that does not exist, and nothing prevents a seventh channel from asking the wrong question again.

## Solution

Give the channel server the same kind of contract the filesystem side already has. A filesystem backend is a type; implementing it is what makes a new filesystem work, and the claim that new filesystems need no agent changes holds structurally. Channels get the equivalent.

A single registration call takes a delivery policy and wires everything a channel server needs: the inbox, the channel-receive tool, the error filter, and a notification emitter. A new transport supplies only the thing that is genuinely transport-specific — how it sends a reply.

The liveness check stops being a property that a caller may or may not remember to read. It becomes the return value of emitting: emitting tells you whether anyone was listening, and the caller decides what that means for its own transport. There is no way to emit without learning the answer, and no way to compute the answer a different way.

Behaviour across all six existing servers is unchanged. This is a consolidation, not a redesign.

## User Stories

1. As a developer adding a new transport, I want a single registration call that wires the whole channel surface, so that I cannot forget a piece of it.
2. As a developer adding a new transport, I want the compiler to require a delivery-policy choice, so that I do not silently inherit a buffering behaviour I never considered.
3. As a developer adding a new transport, I want to write only the reply-sending logic, so that my work is proportional to what is actually novel about my transport.
4. As a developer adding a new transport, I want the channel-receive long poll provided for me, so that I do not copy a tool whose wait-clamping rule I would have to understand first.
5. As a developer adding a new transport, I want the call-tool error filter provided for me, so that I do not accidentally map a long-poll cancellation to an error result and hand the agent's pump something to retry on.
6. As a developer, I want emitting a notification to tell me whether a live subscriber received it, so that I never have to remember to ask separately.
7. As a developer, I want it to be impossible to ask the liveness question the wrong way, so that the stale-buffer defect cannot recur a fourth time.
8. As a developer, I want the difference between the transports and the dual-role servers to be a named policy, so that it reads as a decision rather than as an accident of which enqueue call somebody copied.
9. As a maintainer, I want one channel-receive tool instead of six identical ones, so that a change to the long-poll contract is a single edit.
10. As a maintainer, I want one error filter instead of six identical ones, so that its cancellation rule is stated once.
11. As a maintainer, I want one notification emitter instead of four divergent ones, so that a channel's extra payload fields do not widen a shared interface.
12. As a maintainer, I want the freshness window to be internal to the inbox, so that no caller can substitute its own value.
13. As a maintainer reviewing a new channel server, I want its delivery policy visible at the registration site, so that I can review the choice without reading the emitter.
14. As a maintainer, I want the scheduling and library servers to keep refusing to buffer when nobody is listening, so that a schedule is not deleted and then re-fired against a buffered duplicate.
15. As a maintainer, I want the service-bus server to keep abandoning broker messages when nobody is listening, so that at-least-once redelivery is preserved.
16. As a maintainer, I want the Telegram server to keep buffering unconditionally, so that a message arriving during a cold start is not fanned out to nobody.
17. As a maintainer, I want the signal-relay and voice servers to keep reaching idle-but-unpruned subscribers, so that a brief agent gap does not lose a message.
18. As a maintainer, I want the two thin channel servers to keep depending on the domain project alone, so that they do not acquire a browser automation, a cache client, a console UI toolkit and the whole agent stack as transitive dependencies.
19. As a maintainer, I want the notification payload built with named properties, so that two adjacent optional string parameters cannot be transposed at a call site.
20. As a maintainer, I want the per-server emitter tests to cover only that server's payload shape, so that each transport's extra fields stay locally asserted.
21. As a maintainer, I want the duplicated liveness assertions removed from four test files, so that the rule is asserted once where it lives.
22. As a maintainer, I want the emitter sealed, so that no test seam is carried in production code for the benefit of one integration test file.
23. As a maintainer, I want voice tests to assert against a real inbox, so that they exercise the delivery path rather than an override of it.
24. As a maintainer, I want the two single-adapter emitter interfaces removed, so that consumers in the same project depend on the concrete type.
25. As an end user on a chat transport, I want my message to survive the agent restarting, so that I do not have to resend it.
26. As an end user on a voice transport, I want a brief agent gap not to lose my utterance, so that I do not repeat myself.
27. As an end user with a scheduled task, I want it to fire once, so that a delivery failure does not later produce a duplicate.
28. As an operator, I want a dropped message at inbox capacity to be logged, so that the one irrecoverable loss is never silent.
29. As an operator, I want a channel that cannot reach the agent to behave the way its transport allows, so that a broker-backed transport redelivers while a chat transport buffers.
30. As a future reviewer of this codebase, I want the channel contract to be discoverable from a type rather than from a doc comment, so that I do not have to read six servers to learn it.

## Implementation Decisions

**A new hosting project sits between the domain project and the channel servers.** It references the domain project and the model-context-protocol server package, and nothing else. It cannot reference the infrastructure project: two of the six channel servers depend on the domain project alone today, and pulling in infrastructure would give them a browser automation library, a cache client, a printing library, a console UI toolkit and the whole agent stack as transitive dependencies. The domain project itself cannot host this code, because it deliberately references no dependency-injection or model-context-protocol packages.

**Registration extends the model-context-protocol server builder, not the service collection.** The channel-receive tool and the call-tool filter have to join the builder chain; the inbox and emitter are registered through the builder's service collection from there.

**Delivery policy is a required argument with no default.** It has three values, which together preserve every existing server's behaviour:

- *Broadcast* — always enqueue. Subscribers that are idle but not yet pruned still receive the item. Used by the signal-relay and voice servers.
- *Buffer-always* — enqueue targeted at a known subscriber id, creating that subscriber's queue on demand so an item arriving before the agent's first poll is buffered rather than fanned out to nobody. Used by the Telegram server, which has no transport-level way to tell a sender to retry.
- *Gate-on-live* — enqueue only when a live subscriber exists; otherwise nothing is buffered. Used by the scheduling, library and service-bus servers, whose callers settle a durable record only when delivery is confirmed, and which would otherwise both keep the record and leave a buffered duplicate behind. (The service-bus assignment was corrected during implementation: liveness is only knowable after the emit, so broadcast would leave a buffered copy behind every abandoned broker message.)

The distinction between broadcast and gate-on-live is exactly the no-live-subscriber case. That distinction previously existed only as a difference between which enqueue method a developer had copied.

**Buffer-always requires a subscriber id; the other two policies must not be given one.** Validate this at registration rather than at first emit. The id must match what the agent's channel connection derives for itself, or items are buffered into a queue nobody drains — a failure that is otherwise silent.

**The emitter takes a built notification and returns whether a live subscriber was present.** It has two members, one for message notifications and one for cancel notifications. The liveness check happens inside the operation. This removes the liveness property from six public surfaces, two of which computed it and never read it.

Because callers now build the notification themselves with named properties, the transport-specific fields that previously widened four divergent parameter lists — the per-message configuration patch on the signal-relay server, and the room location, satellite id and dismissed-alert fields on the voice server — become ordinary properties on the shared payload. No interface widens to accommodate them.

**The emitter is sealed.** Its only substitution today is a test subclass in the voice integration tests, which is a test seam carried in production code. Those tests construct a real inbox and drain it instead.

**The freshness window becomes internal to the inbox.** It is currently a shared public constant that every emitter passes in, which is what allowed six near-miss variants to exist.

**Two single-adapter emitter interfaces are removed** — the scheduling and library notification emitter abstractions. Each has one production implementation, no test double, and a consumer in the same project. This does not conflict with the recorded decision to keep single-adapter interfaces in the domain contracts folder: those two live in their server projects and have no domain consumer.

**Nothing about the agent side changes.** The channel connection already feature-detects the optional tools and needs no edit.

## Testing Decisions

A good test here asserts external behaviour: what a subscriber receives, and what the emitter reports back. It does not assert that a particular method was called, and it does not reach past the emitter into inbox internals. The three policies differ only in observable outcomes, so all three are expressible this way.

**Three seams.**

*The channel conformance seam* is the existing integration theory that boots each real server's configuration module and asserts it exposes a conforming channel surface. This is the highest existing seam and the one that would have caught all three rounds of the stale-subscriber defect. It gains a per-server assertion of the declared delivery policy, so the six-row table becomes the place where "this server is a channel server, and this is the policy it chose" is verified against the real registration.

*The policy seam* is domain-level, over the inbox and the emitter together. It covers the three policies' behaviour in the case that distinguishes them — no live subscriber — plus the cases that recur as bugs: an idle-but-unpruned subscriber still receiving under broadcast, a queue created on demand under buffer-always, and nothing buffered under gate-on-live. These should not require booting six servers, because a policy defect is not a server-integration defect.

*The per-server payload seam* keeps one emitter test file per channel, narrowed to that transport's own payload shape. The duplicated liveness assertions in four of those files are removed, since that rule now lives at the policy seam.

**Prior art.** The existing channel-receive contract theory is the model for the conformance seam. The existing per-server emitter tests are the model for the payload seam, minus their liveness sections. The inbox already has domain-level tests covering subscriber eviction, capacity and poll semantics; the policy seam extends that file's style.

**Regression risk to cover explicitly.** The voice arbitration integration tests currently use a lock-protected emitter variant because two connections reach it concurrently. Draining a shared inbox from two connections must preserve that; the inbox is thread-safe, but the assertion helper needs to be as well.

## Out of Scope

- Closing the cold-start gap on the five servers that do not buffer. Three policies preserve current behaviour deliberately; changing any server's policy is a separate, deliberate decision with its own user-visible consequences.
- Agent-registration and conversation-creation support. Four of the six servers expose agent registration and two expose conversation creation, one of them as a no-op stub that exists only so feature detection finds a tool. Unifying those is real work and a separate spec.
- The reply-sending implementations. These are genuinely transport-specific and are the thing a channel server is supposed to own.
- The message-accumulator duplication between the Telegram and service-bus servers.
- The mutable control-writer delegate on the voice satellite session, and anything else in the voice subsystem's turn lifecycle.

## Further Notes

**Cross-spec blocking edge.** The final task here restructures the voice satellite host and wake arbitration integration test files. A separate planned change to the voice turn lifecycle rewrites the same two files. These cannot be worked in parallel; whichever lands second rebases onto the first.

**The git history is the strongest argument for this change** and is worth keeping in front of whoever implements it. Four commits tell the story: the partial first fix, the second round that names the two servers carrying production-affecting variants of the bug, the third round retuning the shared constant, and the test commit that pins "the six servers that regressed". The defect was never hard to fix. It was hard to fix *once*.

**Design decisions were settled by interview** and are recorded in the corresponding plan document, including the alternatives considered and rejected: hosting the registration in the infrastructure project, adding the packages to the domain project, converging on two policies rather than three, and keeping the emitter unsealed to preserve the voice test seam.
