# Spec — Metrics Publishing Module

Status: ready-for-agent

Grilled from candidate 1 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. Decision
recorded as `docs/adr/0002-metrics-publishing-is-fire-and-forget.md`. Vocabulary
follows `CONTEXT.md`: a **metrics publisher** is the fire-and-forget thing a caller
holds, a **metric sink** is the transport it drains into.

## Problem Statement

Publishing a metric returns a task that can fail, so every one of about fifty call
sites has to decide what a failed publish means. They do not agree.

Nine sites implement a guard. Five are named helper methods with four different
signatures, spread across the satellite host, the announcement service, the
insistent announcement controller, the noise-extraction speech-to-text decorator
and the agent. Four more are inline catch blocks in the tool approval chat client,
the memory recall hook and the chat message store. Each carries a comment
restating the same rule: metrics are best-effort and must never fail a turn. The
other forty-odd sites state the rule in a comment or not at all, and do nothing
about it.

The rule is real, but the guarantee does not live anywhere a caller can see. It
lives in which publisher the host registered. The agent host registers the
buffered publisher, whose publish only writes to an in-memory channel and cannot
throw, so all nine guards are dead code in that process. The voice host registers
the Redis transport directly as the publisher, so a publish is a live Redis round
trip and the guards are the only thing between a Redis blip and a broken turn.

Two sites in the voice host are unguarded, and both are live defects. The satellite
host publishes its speech-to-text latency inside the try block whose catch logs a
transcription failure and returns false, so a Redis blip discards a good
transcript. The error publish inside that catch escapes into the follow-up
conversation loop, which handles only cancellation. The conversation task dies
while the connection to the satellite stays up.

A second convention sits on top of the first. Eleven declarations type the
publisher as nullable and six production sites guard it before use, even though
every production construction path passes a real one. The nullability exists so
tests can omit it.

There is also a measurement pattern nobody owns. Six sites start a stopwatch, stop
it, and publish a latency event carrying a stage, an elapsed duration and a
conversation id. Four of those reuse the same elapsed value for a second,
domain-specific event. The tool approval chat client publishes the identical
latency block twice, once in each branch of a try/catch.

No test covers "a throwing publisher does not kill a turn".

## Solution

Put the guarantee in the type, so no call site restates it and no host can register
its way out of it.

A metrics publisher exposes one void method. A caller cannot await it, cannot catch
it, and cannot pass it a cancellation token. There is nothing at a call site to get
wrong, and the nine guards have nothing left to guard.

Publishing to Redis is still a real network operation that really fails, so that
role gets its own type. A metric sink sends an event and may throw. The buffered
publisher is the only metrics publisher a host registers, and it drains into the
sink on a background reader, logging whatever the sink refuses.

The two roles split along a line that already exists: the thing callers hold makes
a promise, and the thing behind it does the work that can break the promise.

Hosts stop assembling this by hand. One registration call wires the sink, the
buffered publisher and the heartbeat service together, which makes the voice defect
unrepresentable — no path resolves a bare sink as the caller-facing publisher.

Measuring a span becomes a scope rather than a convention, so a latency event is
published once whether the measured work returns or throws.

## User Stories

1. As a developer publishing a metric, I want a call I cannot await, so that I do not have to decide what a failed publish means.
2. As a developer publishing a metric, I want a call I cannot wrap in a catch block, so that I do not write the tenth variant of the same guard.
3. As a developer publishing a metric, I want the publisher to be non-null, so that I do not guard it before every use.
4. As a developer publishing a metric, I want no cancellation token to pass, so that I cannot accidentally suppress an event by handing it the token of the thing that just got cancelled.
5. As a developer measuring a span, I want a scope that publishes on both the return path and the throw path, so that I do not duplicate the emission per branch.
6. As a developer measuring a span, I want the elapsed value available from the scope, so that a domain event can carry the same duration without a second stopwatch.
7. As a developer measuring a span, I want the scope to end where the enclosing block ends, so that an early return cannot silently skip the measurement.
8. As a developer adding a new host that publishes metrics, I want a single registration call, so that I cannot register a transport where a buffered publisher belongs.
9. As a developer adding a new host that publishes metrics, I want the heartbeat wired by the same call, so that a service reporting metrics cannot be missing from the health roster.
10. As a developer writing a transport for a different metrics backend, I want a sink contract that is allowed to fail, so that I do not swallow errors inside my own adapter.
11. As a developer reading an unfamiliar call site, I want the never-fail rule visible in the signature, so that I do not have to trace the host registration to know whether a publish can throw.
12. As a maintainer, I want the rule stated once in a type rather than thirty-five times in comments, so that a change to it is one edit.
13. As a maintainer, I want the nine hand-rolled guards deleted, so that a reader does not have to work out whether each one is live or dead.
14. As a maintainer, I want the methods that exist only to guard-and-publish removed, so that the call graph shrinks to the sites that actually measure something.
15. As a maintainer, I want methods that were async only because a publish was awaited to become synchronous, so that the async surface reflects real asynchronous work.
16. As a maintainer, I want a test that boots each host's real registration, so that the next host to wire this wrongly fails a test rather than production.
17. As a maintainer, I want a throwing sink not to kill the drain loop, so that one bad event does not stop all later metrics.
18. As a maintainer, I want the comments that reason about publish cost or publish failure rewritten, so that a reader is not led by a comment describing behaviour that no longer exists.
19. As a maintainer, I want the reason the publisher is not async recorded as a decision, so that a reviewer does not "fix" it back into an awaitable.
20. As a maintainer, I want the nullable-publisher convention gone without touching seventy-four test construction sites, so that the cleanup is not paid for in unrelated churn.
21. As an operator, I want a Redis blip never to discard a good transcript, so that a voice turn survives an unrelated infrastructure hiccup.
22. As an operator, I want a Redis blip never to kill a satellite conversation task, so that a satellite does not sit with a live connection and a dead conversation.
23. As an operator, I want a cancelled turn to record its metrics, so that I can see how long a turn ran before the user gave up on it.
24. As an operator, I want a dropped event at buffer capacity logged, so that the one irrecoverable loss is not silent.
25. As an operator, I want metrics published during shutdown drained where possible, so that the last events of a run are not routinely lost.
26. As an operator, I want a metric publish never to sit inside a turn's measured latency, so that the dashboard is not reporting its own overhead back to me.
27. As a dashboard user, I want the same events in the same shape on the same channel, so that history and live updates are unaffected by this change.
28. As a future reviewer, I want the distinction between a publisher and a sink written into the glossary, so that the two contracts do not read as redundant.

## Implementation Decisions

**The metrics publisher contract becomes a single void publish method.** No task,
no cancellation token. This was chosen over a best-effort decorator wrapping the
existing awaitable contract. A decorator removes the duplication but leaves an
awaitable at every call site that a caller can still await, catch, or forget to
register the decorator for — and forgetting the registration is exactly what went
wrong in the voice host. Nothing in the codebase reads the result of a publish or
depends on one completing; the heartbeat service awaits only inside its own timer
loop. The signature therefore costs no capability.

**Transports implement a separate metric sink contract**, which sends an event
asynchronously and is allowed to throw. The Redis transport is its one adapter.
This contract lives in the infrastructure layer, not in the domain contracts
folder, because the domain layer never consumes it — the domain-layer rule scopes
contract interfaces to services the domain uses, and only infrastructure implements
or drains a sink. This does not conflict with ADR 0001: that record governs the
domain contracts folder and is the reason a one-adapter sink contract is acceptable
rather than a bare delegate.

**The buffered publisher is the only metrics publisher a host registers.** It keeps
its bounded channel, its drop-on-full behaviour, its warning log for a dropped
event, and its bounded drain on disposal. It takes a sink instead of an inner
publisher.

**A no-op publisher replaces the nullable convention.** A shared no-op instance is
coalesced once where the publisher is stored, and the stored field is non-nullable.
The optional parameter with a null default stays, so all seventy-four test
construction sites are untouched, but the six production null guards go. Making the
parameter required was considered and rejected on churn: the coalesce achieves the
same deletion for none of the cost.

**The cancellation token is gone, and cancelled turns now record their metrics.**
Today the buffered publisher drops any event whose token is already cancelled, so a
cancelled turn silently loses its schedule-execution event and its first-reply
latency. That was never a decision anyone made. After this change those events are
recorded and the dashboard shows data for cancelled turns that it previously did
not. Process shutdown remains bounded by the existing drain on disposal, so no
separate host lifetime token is introduced.

**One registration call wires the whole surface**, taking the service name and
registering the sink, the buffered publisher and the heartbeat service. This
mirrors the channel-server registration idiom already in the codebase. It makes the
voice defect structurally impossible, and it couples publishing metrics to
appearing on the health roster, which is what the observability design already
assumes.

**Latency measurement becomes a scope.** Opening one takes the stage and the
conversation and agent identifiers; disposing it publishes the latency event. The
scope exposes its elapsed duration for the four sites that also emit a
domain-specific event carrying the same value. Publishing on dispose covers the
return path and the throw path with one statement, which collapses the tool
approval chat client's duplicated block. One site needs care: the memory recall
hook starts its stopwatch before an early-return guard, so its scope must open
after that guard. That is a reorder past a string emptiness check and changes
nothing measurable.

**Synchronousness cascades outward until the first method that still awaits real
work.** Six methods exist only to guard and publish, and disappear entirely: the
chat monitor's first-reply latency helper, the agent's safe latency helper, the
satellite host's verification-latency and unknown-speaker helpers, and the two
remaining safe-publish shapes. Any enclosing method left with no awaits loses its
async task signature and its `Async` suffix, and its callers stop awaiting it. The
cascade stops at the first method that awaits something real.

**Two sites in the insistent announcement controller need hand edits** rather than
a mechanical replacement: one fans out over offline targets by gathering a task per
publish, which has no task left to gather, and one passes the publish as a callback
typed to return a task, which now needs an explicit completed task.

**Comments that reason about publish cost or publish failure are rewritten.** The
reply tool deliberately takes a turn timestamp before its publish because the
publish is an awaited Redis round trip whose cost would otherwise fall outside every
span; after this change the publish is a channel write and that care is
unnecessary. The reply tool's preemption path, the satellite host's early-reject and
diagnostic-publish comments, and the noise-extraction decorator's catch-all comment
all reason about a metrics failure that can no longer happen.

**Nothing on the consumer side changes.** The collector service, the query service
and the dashboard read the same events, in the same shape, off the same Redis
channel.

## Testing Decisions

A good test here asserts external behaviour: what a caller can observe after
publishing, and what a host resolves. It does not assert that a particular method
was called, and it does not reach into the buffer's internals. The properties that
matter are all expressible that way.

**Two seams, both as high as the behaviour allows.**

*The host registration seam* is the highest one available and is the only place the
reported defect is visible. It is an integration theory that boots each host's real
registration module and asserts the resolved metrics publisher is the buffered one,
never a bare sink. This is the test that would have caught the voice defect, and it
catches the next host too. Prior art is the channel receive contract theory, which
boots every real channel configuration module and asserts its declared delivery
policy; the filesystem server conformance tests are the same idea applied per
server.

*The publisher seam* is unit-level, over the buffered publisher and a fake sink. It
covers the three properties the contract now promises: a throwing sink does not
escape a publish, a throwing sink does not stop the drain loop from handling later
events, and a full buffer drops and logs rather than throwing or blocking. Prior art
is the existing buffered publisher test file, whose tests change shape with the
signature but keep their intent.

**The measured scope is tested at the publisher seam**, against a recording
publisher: publishes once on the return path, publishes once on the throw path, and
reports an elapsed value consistent with the event it published. No new seam.

**Red first.** The registration theory is written against the current wiring, where
the voice host resolves a bare Redis transport as its publisher, and must be seen to
fail before the registration call exists.

**Existing tests move with the signature, not around it.** Five test files define
six recording publishers of their own, and two more use mock setups on the
awaitable method; these become simpler, not more numerous. No test gains a wait or
a poll, because a recording publisher records synchronously.

**A voice-level regression test is deliberately excluded.** A test asserting that
the satellite host returns a transcript when the sink throws would pin the reported
defect most directly, but the spans it needs are only reachable through the hosted
service today. That testability problem is its own candidate. The registration
theory plus the contract change already make the defect unrepresentable.

## Out of Scope

- The voice host's testability. The satellite host's transcription path cannot be driven without the hosted service; deepening that is a separate candidate and blocks any turn-level voice regression test.
- The reply tool's turn-stamping and segment-token design. Its comments are corrected here, but the mechanism is a separate candidate.
- Which metric events exist and what dimensions they carry. This changes how an event is published, never what is published.
- The metrics collector, the query service, the health roster and the dashboard.
- Making the publisher parameter required rather than optional-with-coalesce.
- Any second metric sink. One adapter is expected and, per ADR 0001, sufficient.

## Further Notes

**The strongest argument for the contract change is that the guards are already
wrong in both directions.** In the agent host all nine protect against a publisher
that cannot throw. In the voice host they are load-bearing, and the two sites that
lack them are the live defect. A rule enforced by convention ended up applied in
exactly the wrong pattern, and no reader could tell which process they were looking
at without tracing the registration.

**Design decisions were settled by interview.** Alternatives considered and
rejected: a best-effort decorator over the existing awaitable contract; registering
the buffered publisher in the voice host and changing nothing else; documenting the
guarantee and requiring each adapter to honour it; a required non-nullable publisher
parameter; a bare delegate instead of a sink contract; a host lifetime token so that
shutdown drops events explicitly; and an extension method over the stopwatch triple
instead of a scope.

**Documentation lands with the implementation.** The decision record and the
glossary entries are already written. The observability rule file describes the
current shape and is what an agent reads before touching metrics, so it is updated
in the same change as the code, not before it.
