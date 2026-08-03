# Spec — The Hub Call Surface

Status: ready-for-agent

Grilled from candidate 5 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. The disconnected
rule is recorded as `docs/adr/0004-hub-calls-answer-or-say-not-live.md`. Vocabulary
follows the "Chat client connection" section of `CONTEXT.md` — **live connection**,
**hub connection**, **hub call**, **not live**, **becoming live**, **connection
epoch**.

Sequenced after `.scratch/chat-live-connection/`, which renames the module, adds the
receive verb to the hub connection abstraction and gives its fake a handler registry.
This spec adds the send verbs to the same seam and removes the raw connection
accessor that spec kept deliberately and temporarily.

## Problem Statement

A WebChat user picks a different agent a few seconds after their phone wakes up. The
sidebar empties. Nothing was deleted — the client was between connections, the topic
fetch came back with an empty list, and the effect stored it as the truth.

The same window costs more than a sidebar. A message typed into it disappears with no
error and no bubble. A message sent while a reply is already streaming opens a second
stream that produces nothing, and the user is shown a stream that started and will
never say anything.

All three have one cause. The connection object is exposed raw on the client's
connection interface, and seventeen hub calls across six services reach through it.
Each repeats the same three lines: fetch the connection, null-check it, and on null
return an empty list, `false` or `null` — the same value the server itself returns
when there is genuinely nothing there. "We could not ask" and "the answer is nothing"
are one value, and no signature anywhere says so. The interesting fact about the
whole surface is the one fact it does not express.

The guard does not even cover the whole window it was written for. The connection is
null only between a teardown and the next successful start. While the transport is
connecting or reconnecting it is non-null and not active, so the null check passes
and the call goes to a transport that cannot carry it.

None of it is under test. Five of the six services take the concrete connection type
and reach a raw transport object, so none of them can be faked, and the fifteen
disconnected branches have no coverage at all. Three of the interfaces have been
written a second time in the integration test project against a bare transport; those
copies have no references from anywhere and are dead.

## Solution

Every hub call comes back with one of two things: the server's answer, or **not
live**. The client can finally tell an empty list from an unasked question.

The live connection is the only thing that can decide, because it owns the transport
instance and knows whether it is live. The call verbs move onto it, and the raw
connection accessor disappears — from the live connection and from the hub connection
abstraction both, leaving SignalR's own types inside the factory that builds them.

For the user, the client stops lying in three places. Switching agents during a
rebuild leaves the sidebar alone and it refills when the connection is back. A
message that could not be sent says so, once, instead of vanishing. A send that could
not be enqueued no longer opens a stream that will never speak.

The six services stay, and get smaller: each method becomes the one line that names a
wire call and its arguments, with no null handling left in it. The effects do the
deciding, under one rule that follows from who asked for the call. A read that feeds
a store skips its dispatch and leaves the state it already has, because the
connection epoch reloads everything on becoming live anyway. A call the user
initiated raises one error toast.

## User Stories

1. As a WebChat user, I want switching agents while the client is reconnecting to
   leave my conversation list alone, so that a momentary interruption does not look
   like my conversations were deleted.
2. As a WebChat user, I want a message I type during an interruption to tell me it
   did not send, so that I do not sit waiting for a reply to something the server
   never received.
3. As a WebChat user, I want a message sent while a reply is streaming to either
   reach the server or say it did not, so that I am never shown a stream that has
   already failed.
4. As a WebChat user, I want deleting or renaming a conversation during an
   interruption to tell me it did not happen, so that I do not believe a change that
   was never made.
5. As a WebChat user, I want answering an approval prompt during an interruption to
   tell me it did not reach the agent, so that I can answer again rather than assume
   the agent is unblocked.
6. As a WebChat user, I want cancelling a running reply during an interruption to
   tell me it did not stop, so that I am not surprised when the reply continues.
7. As a WebChat user, I want my transcript to stay on screen when a history fetch
   cannot be made, so that an interruption does not blank the conversation I am
   reading.
8. As a WebChat user, I want the agent list to keep its contents when it cannot be
   fetched, so that the agent picker does not empty itself mid-interruption.
9. As a WebChat user, I want the failure message to appear once however many calls
   failed, so that a resume does not bury the screen in toasts.
10. As a WebChat user, I want everything that could not be fetched during an
    interruption to arrive on its own once I am back, so that I never have to reload
    the page to catch up.
11. As a WebChat user, I want recovery work I never asked for to stay silent when it
    cannot run, so that rejoining a space or re-registering does not produce error
    messages I can do nothing about.
12. As a developer, I want a hub call to say when it could not be made, so that I
    cannot mistake an unasked question for an empty answer.
13. As a developer, I want that to be true of the streaming calls too, so that "the
    agent said nothing" and "we never asked" are not the same empty sequence.
14. As a developer, I want to know a stream could not start before I iterate it, so
    that I do not announce a stream that will produce nothing.
15. As a developer, I want a server answering "no" to stay distinct from a call that
    was never made, so that a refused session and an unreachable one are handled
    differently.
16. As a developer, I want the not-live decision made in one place, so that adding a
    hub call cannot invent a new convention for it.
17. As a developer, I want the connecting and reconnecting states covered by the same
    decision as the null one, so that the rule does not have the hole the guards had.
18. As a developer, I want the raw connection object off both interfaces, so that no
    caller can reach past the decision.
19. As a developer, I want SignalR's client types confined to the factory, so that
    the rest of the client is written against seams it can fake.
20. As a developer, I want the calling services to be typed method lists, so that
    reading one tells me what the server offers and nothing else.
21. As a developer, I want the services to depend on an interface rather than the
    concrete connection, so that they can be faked at all.
22. As a developer, I want every wire call reachable through a named method, so that
    no effect holds a wire name of its own.
23. As a developer, I want to fake the transport and keep the live connection, the
    services, the effects and the stores real, so that my tests describe what a user
    would see rather than which methods were called.
24. As a developer, I want one fixture that wires the client to a scripted transport,
    so that writing a not-live test is a few lines rather than a composition
    exercise.
25. As a developer, I want to assert that a store survives a not-live read, so that
    the sidebar defect has a regression test.
26. As a developer, I want to assert that a not-live user action raises exactly one
    toast, so that the silent-failure defect has one too.
27. As a developer, I want the duplicated hub-call adapters in the integration tests
    deleted, so that nobody maintains a second copy of an interface nothing uses.
28. As a developer new to the client, I want "not live" to be one written-down term,
    so that I do not invent disconnected, offline and failed for the same fact.

## Implementation Decisions

### The result

```csharp
readonly record struct HubResult<T>(bool IsLive, T? Value);
```

Two cases and no more: the server's answer, or not live. Not live never means the
server said no — a server that answers `false` is live and has answered.

The name follows `CONTEXT.md`. A reader who knows what a live connection is already
knows what `NotLive` means, which an alternative like "unreachable" would not give
them.

`HubResult<bool>` therefore carries three outcomes, and the callers of the session
start, the message enqueue and the approval response must keep the middle one
distinct from the first. That is the point: today they cannot.

### The verbs

The live connection gains exactly the three verbs the client uses — a typed invoke, a
void invoke and a stream — each returning a hub result. No send verb: nothing in the
client calls one, and adding it for parity with the transport would make the
interface as wide as the implementation again, which is the defect this spec is
fixing.

The same three verbs go on the hub connection abstraction, so the module can be
driven against a fake and the fake can answer calls. The probe stays exactly as it is
and does not return a hub result: it is what asks whether the connection is live, so
it cannot be an answer that depends on the connection being live.

With the receive verb from the previous spec and these three, nothing reads the raw
connection object any more. It is deleted from the live connection interface — the
accessor this candidate is named for — and from the hub connection abstraction. The
transport's connection-state enum stays where it is used, on the hub connection's
state property and in the foreground reconnect policy.

### The services

All six stay. The topic, messaging, approval, agent, session and push services become
typed method lists: one line each, naming a wire call and its arguments, returning
whatever the verb returned. Every null check in them is deleted. The five that take
the concrete connection service take the live connection interface instead, which is
what made them untestable and is the reason they have no unit tests today.

Their interfaces change with them: each method returns a hub result, including where
it returned a bare task. The two streaming methods return a task of a hub result
wrapping the sequence, and stop being iterator methods, so a caller learns the
outcome before iteration rather than by iterating nothing.

The user registration call, the one hub call with no service, joins the session
service. It is the same concern as starting a session — the client's session with the
server — and it gives the session recovery introduced by the previous spec a typed
dependency it can fake.

### The effect rule

Who asked for the call decides what happens when it comes back not live.

**A read that feeds a store skips its dispatch.** Topics, history, agents, stream
state and pending approvals leave the store holding what it already had. Nothing
further is needed: the connection epoch reloads topics, history and streams on
becoming live.

**A call the user initiated raises one error toast.** Sending, enqueuing, starting a
session for a send, saving, deleting, cancelling and answering an approval. The toast
store already dedupes by message, so one toast text for all of them is one toast on
screen however many calls failed in the same window.

**A call the client made for its own reasons does neither.** Joining a space,
registering the user, resuming a stream and the push resubscribe are recovery steps;
they are retried on becoming live and the user did not ask for them.

The one place this changes control flow rather than just adding a branch is the
send-or-enqueue path in the streaming service. A not-live enqueue must not fall
through to starting a new stream — today it does, because it cannot tell that answer
from a `false` meaning there is no stream to enqueue onto.

### The dead adapters

The three hub-call adapters in the integration test project are deleted. They are a
second copy of three client interfaces written against a bare transport, and nothing
references them from anywhere in the solution.

## Testing Decisions

A good test here asserts what a user would notice: the sidebar still has topics in
it, one toast is on screen, no stream was announced. Not that a method was called,
and not that a flag flipped. The defects this spec fixes all survived a suite that
asserts calls.

### Seams

One. The hub connection abstraction is faked; the live connection, the six services,
the effects, the dispatcher and the stores are all real. That is the same seam the
chat live connection spec picked, and it is the highest one available — everything
between a transport that cannot carry a call and the state a user sees is exercised
as a unit. It also covers the wiring itself: a service that forgets to pass not-live
through fails a test, which a service-level fake could never catch.

This needs a composition fixture that does not exist today — the client wired to a
scripted transport, with the stores it dispatches into exposed for assertions. Build
it once; every test below is a few lines on top of it. The existing fake hub
connection, extended by the previous spec with a handler registry, is what the
fixture scripts.

### What gets tested

The live connection's verbs, directly: each returns the server's answer when live,
and not live when the connection is null, connecting or reconnecting. The last of
those is the hole in today's guards and is worth writing first.

Then one behavioural assertion per defect, through the fixture. A topics fetch that
cannot be made leaves the existing topic list in the store untouched — the headline
test, and it must fail against the current design before anything is extracted. A
history fetch that cannot be made leaves the transcript. A send that cannot be made
raises exactly one toast and adds no message. An enqueue that cannot be made does not
start a new stream and announces nothing. Two failed user actions in the same window
produce one toast, not two. A read that cannot be made raises no toast at all.

And the recovery rule: a space join or user registration that cannot be made is
silent, and does not disturb the stores.

The push notification service keeps its existing suite, which tests its JS-interop
branches and gains nothing here.

### Prior art

`ChatConnectionServiceTests` and its scripted connection factory are the model for
the live connection's verb tests and for the new fixture's transport scripting.
`AgentSelectionEffectTests`, `SendMessageEffectTests` and `StreamingServiceTests`
describe the scenarios being re-asserted, and are where the not-live cases belong.
`TestChat.Eventually` is how a test waits for state produced by a fire-and-forget
dispatch.

The `Fake*Service` fixtures stay for the tests that already use them; they are not
the seam for the new tests, and their interfaces change shape with the services.

Follow red-green-refactor.

## Out of Scope

Everything the chat live connection spec owns. It lands first.

The session service's current-topic property and its change event. They are client
state living outside the store, read directly by the send-message effect. Moving them
into a store is a state-placement argument with nothing to do with the call surface,
and this spec leaves them where they are.

The push service's unsubscribe swallowing errors from the server call. The
client-side subscription is already gone at that point, so the server-side cleanup
failing is not something to tell the user about.

The wire calls themselves. No method name, argument or return shape on the hub
changes, and no server-side file is touched.

The connection indicator, the retry policy, the probe and the rebuild bounds. All
preserved exactly.

## Further Notes

The candidate lists "five untestable modules become unit-testable" as the main test
win. That was grilled down twice. The services are worth making fakeable, which they
are not today, but they are not worth testing once they are one line each — and with
a single seam at the transport they are not faked either, they are real. The value is
that the effects can now be tested, and the effects are where all three defects live.

The candidate also raises ADR-0001, which rejects adapter counting as grounds for
deleting an interface. It does not apply here and nothing in this spec deletes a
seam. All six services keep their interfaces; they get narrower, and five of them
stop depending on a concrete type.

Vocabulary was written into `CONTEXT.md` during the grilling session: **hub call**
and **not live**, under "Chat client connection". Use those terms in code, comments
and tickets. In particular, do not call it "disconnected": the client can be
perfectly online, mid-rebuild, and a hub call still cannot be made.
