# Spec — Resolve the Config Patch Once

Status: done

## Problem Statement

A WebChat user can override the agent's model and reasoning effort for a single message. The two fields travel together in one small patch object attached to that message. Nothing else in the system produces one.

They are then read back out in two different places, by two modules that do not know about each other, under two different rules. The agent reads the reasoning half and applies it to the turn. The chat client reads the model half, checks it against a whitelist, and parks the answer in a field that belongs to the client rather than to the request. The client is shared by every turn that agent runs.

The parked value is what breaks. Turns within one conversation are consumed concurrently today, so a second turn can overwrite the field between the moment the first turn writes it and the moment the outgoing HTTP request reads it. The first request then goes out on the wrong model, and the metrics stamp reports whichever write won the race. The user asked for one model, got another, and the dashboard agrees with neither.

The two rules diverge in a way users notice. A model that is not on the whitelist is rejected and the configured model is used, silently. A reasoning effort that cannot be parsed is swallowed by a caught exception with no log at all. In both cases the user's request is quietly ignored and nothing anywhere says so.

Coverage is arranged so that this is invisible. The chain has tests at four disjoint points — the wire format, the message stamping, the whitelist function in isolation, and latency attribution — and no test spans them. Deleting the line that stamps the patch onto the user message, or the line that applies the reasoning half, leaves the entire unit suite green.

## Solution

Resolve both fields once, at the single point that already resolves one of them, and let the answer ride the request instead of sitting on the client.

The agent resolves the model and the effort together when it builds the options for a turn. The resolved model travels with that turn's options to the outgoing request. Nothing about the patch is stored on a shared object, so no second turn can disturb it, and the model reported in metrics is by construction the model the request used.

Both fields get one rejection rule: fall back to the configured value and log a warning naming the field, the rejected value, and the fallback. A user whose override was dropped leaves a trace.

Turns within a single conversation are consumed one after another instead of concurrently. This is what the rest of the stack already assumes; the change makes the assumption true rather than accidental, and it removes two further shared-state hazards without touching them.

One test spans the agent's turn options through to the outgoing request body, so the path cannot be broken silently again.

## User Stories

1. As a WebChat user, I want the model I pick for a message to be the model that answers it, so that my choice is not a suggestion.
2. As a WebChat user, I want two messages sent in quick succession to each use the model I picked for them, so that hurrying does not change the answer I get.
3. As a WebChat user, I want a reasoning effort I pick to be applied to that message, so that a hard question gets the thinking I asked for.
4. As a WebChat user, I want to keep getting an answer when my override is refused, so that a bad setting never costs me a turn.
5. As a WebChat user, I want my messages in one conversation answered in the order I sent them, so that a follow-up is never answered before the message it follows.
6. As a WebChat user, I want an override to apply only to the message I attached it to, so that it does not leak into my next message.
7. As a user on a channel that sends no patch, I want my turns unchanged by this work, so that the configured model and effort still govern.
8. As an operator, I want the model recorded against a turn's latency to be the model the request actually ran on, so that per-model timings are trustworthy.
9. As an operator, I want the model recorded against token usage and cost to be the model that produced them, so that spend is attributed correctly.
10. As an operator, I want a rejected model override logged with the value and the fallback, so that I can tell a user why their choice did not take.
11. As an operator, I want a rejected reasoning effort logged the same way, so that the quieter of the two failures stops being invisible.
12. As an operator, I want a client whitelist that has drifted from the agent's whitelist to be visible in the logs, so that I can fix the configuration rather than guess.
13. As a maintainer, I want both patch fields resolved in one place, so that a third field can be added without choosing between two conventions.
14. As a maintainer, I want the resolved patch carried on the turn's options, so that no per-request value lives on an object shared by every request.
15. As a maintainer, I want the mutable override holder deleted, so that the race cannot be reintroduced by a future caller reading it.
16. As a maintainer, I want the effective-model report derived from the turn's options, so that it cannot disagree with the request.
17. As a maintainer, I want one rejection policy for both fields, so that a reader does not have to check which field they are looking at to know what happens.
18. As a maintainer, I want turn consumption within a conversation to be sequential, so that the reasoning, cost and cached-token queues drained per response cannot cross-attribute between interleaved streams.
19. As a maintainer, I want the dynamically-approved tool set no longer mutated from concurrent turns, so that an unsynchronised collection stops being a latent corruption.
20. As a maintainer, I want a comment at the serialisation point recording what depends on it, so that anyone reintroducing concurrency knows what they are re-breaking.
21. As a maintainer, I want fan-out across multiple delivery targets to stay concurrent, so that serialising turns does not slow multi-target delivery.
22. As a maintainer, I want one test spanning the agent's options to the outgoing request body, so that deleting a single line in the middle of the chain fails a test.
23. As a maintainer, I want the whitelist rules asserted through the agent's real output, so that the rules are pinned to behaviour rather than to a helper's signature.
24. As a maintainer, I want reasoning-effort rejection cases covered, so that the field that never had them stops being the untested half.
25. As a maintainer, I want an externally supplied set of run options to be visibly abnormal, so that a caller who bypasses instructions, tools, reasoning and the patch does not do so silently.
26. As a developer adding a new patchable field, I want one resolution site and one validation rule to extend, so that the work is proportional to the field.

## Implementation Decisions

**Both fields resolve where the agent builds its run options.** That method already resolves the reasoning half; the model half moves there from the chat client. The patch is read from the last user message in the turn, as it is today.

**The whitelist moves with the resolution.** The list of patchable model ids is currently passed to the chat client's constructor and consulted there. It becomes an agent-side input. Resolution keeps its current semantics exactly: a patch naming the configured model is not an override; a patch naming a model outside the whitelist is refused; a whitelisted model matched case-insensitively is returned in the whitelist's own casing, because provider model ids are lowercase slugs and echoing the caller's casing can turn a valid override into a model-not-found error.

**The resolved model rides the turn's chat options.** The preferred carrier is the options' own model-id field. Whether that field survives the inner provider client down to the request-preparation helper must be verified against a real captured request body during implementation, not assumed. If it does not survive, the model is carried on the options' additional-properties bag and read per-request in the delegating handler. Either way it is read from the request's own options; under no circumstances is it stored on client-level state.

**The mutable override holder is deleted.** The internal box type and its volatile field go away together with the client-side resolution.

**The effective-model report is derived from the turn's options rather than from ambient state.** The agent's latency and token-usage events therefore stamp the model that the request being measured actually used.

**One validation policy: fall back and warn.** Today a non-whitelisted model falls back silently and an unparseable effort is swallowed by a caught exception with no log. Both keep fallback semantics — a bad override never costs the user a turn — and both emit a warning naming the field, the rejected value and the fallback. The plan fixes the message shape:

```csharp
logger.LogWarning("Rejected config patch {Field}={Value}; using {Fallback}", field, value, fallback);
```

**Turns serialise within a conversation group.** The inner concurrent merge over a group's messages is replaced with sequential consumption. The outer merge across conversation groups and the fan-out across delivery targets are untouched; different conversations still run concurrently.

Serialising is load-bearing for three other pieces of shared state, which is why none of them needs its own fix: the tool-approval client's dynamically-approved set is an unsynchronised collection mutated during a turn, and the chat client's reasoning, cost and cached-token queues are drained per update and per response, cross-attributing between interleaved streams of the same client. A comment at the serialisation point records this dependency.

**Provider routing is untouched.** It is enforced on every turn including model-override turns, and a conflict between routing and an override is a configuration error, never a silent drop. Nothing in this change goes near the routing node of the request body.

**The run-options bypass becomes visible.** The agent currently builds run options only when the caller supplied none, so an externally supplied set silently skips instructions, tools, reasoning and now the patch. Pre-built options arriving from a caller are surfaced rather than accepted in silence.

## Testing Decisions

A good test here asserts what leaves the module: the options a turn runs with, the JSON that goes on the wire, the order in which turns execute, and the warning an operator would see. It does not assert that a particular internal helper was called, and it does not read the client's internal state to decide whether resolution happened.

**Three seams.**

*The turn-ordering seam* is the existing chat-monitor behaviour test file, which is where the multi-target fan-out concurrency test already lives. Two messages queued for one conversation must produce non-overlapping turn windows, and the existing fan-out test must still pass unchanged. Putting both assertions in one file makes the trade explicit: this axis serialises, that axis does not.

*The resolution seam* is the existing config-patch class in the agent's reasoning tests, which already asserts that a patched effort overrides the configured one and that an invalid effort falls back. It absorbs the model half. The five whitelist cases currently asserted against the static helper — whitelisted override, non-whitelisted refusal, patch matching the configured model, absent patch, case-insensitive match returning canonical casing — are re-expressed as assertions on the options the agent produces, and the standalone helper test file is deleted. The effort-rejection cases that never existed are added alongside, and both fields' warnings are asserted here.

*The request-body seam* is the existing chat-client test that drives a real client through a capturing transport handler and asserts against parsed outgoing JSON. It gains the span this change exists to create: a real agent over a real chat client, given a user message carrying a patch, must put the patched model in the outgoing body. Deleting the line that applies the patch to the options fails this test. The span deliberately starts at the agent rather than at a channel message — the monitor's stamping of the patch onto the user message is already covered by its own test, and reaching further would pull MCP session setup into a unit test for no additional defect caught.

**Prior art.** The capturing-handler test is the model for the request-body seam; it is the only existing test that proves constructor-supplied values survive the hop onto a real outgoing request, and it already parses the body as JSON and asserts on named properties. The agent's latency tests are the model for asserting an event's stamped model. The monitor's existing fan-out concurrency test is the model for asserting turn windows.

**What is not moved.** The monitor's patch-stamping test and the agent's latency-attribution tests stay where they are and keep passing. The wire-format tests for the patch's serialisation are unaffected.

## Out of Scope

- Adding new patchable fields. The patch stays two fields.
- Reworking provider routing, or the interaction between an override and a routing constraint.
- The whitelist's configuration shape and the way it is surfaced to clients in the agent catalogue.
- Making the reasoning, cost and cached-token queues correct under concurrency. Serialising turns makes them correct in practice; hardening them for a concurrency that no longer exists is speculative work.
- Synchronising the tool-approval client's dynamically-approved set, for the same reason.
- Restoring concurrent turns within a conversation behind a flag. Serialisation is the decision, not a temporary measure.
- Prompt-cache behaviour and session identity.

## Further Notes

**Serialising turns is an observable behaviour change.** Two messages sent in quick succession to the same conversation are answered in order rather than concurrently. This was raised and accepted: it is what the rest of the stack already assumes, and the alternative is defending shared state in three separate modules against a concurrency nobody wanted.

**The options model-id field is the one real unknown.** It may not survive the inner provider client. The verification must be a real captured request body, not a unit assertion that the options object holds the value; asserting the options alone would pass while the wire stayed wrong, which is the exact failure this spec is written to end.

**Design decisions were settled by interview** and are recorded in this feature's plan document, including why resolution moves to the agent rather than being made thread-safe where it is.
